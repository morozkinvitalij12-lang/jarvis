using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace Jarvis;

internal sealed class Listener : IDisposable
{
    private readonly int _targetRate;
    private readonly WaveInEvent _mic = new();

    public Listener(int deviceId, int targetRate)
    {
        _targetRate = targetRate;
        CaptureRate = targetRate;
        _mic.DeviceNumber = deviceId;
        _mic.DataAvailable += OnData;
    }

    private readonly ConcurrentQueue<string> _utterances = new();
    private readonly object _sync = new();
    private readonly StringBuilder _free = new();
    private readonly List<(long EndTick, short[] Samples)> _frames = [];
    private Native.Recognizer? _recognizer;
    private Native.Recognizer? _alternate;
    private long _lastSpeechTicks;
    private long _buffers;
    private volatile string _partial = string.Empty;
    private volatile string _freePartial = string.Empty;

    public int CaptureRate { get; private set; }

    /// <summary>Нейросети нужна вся фраза сразу, поэтому держим в памяти последние 30 секунд звука.</summary>
    private const long CaptureHorizonMs = 30_000;

    public long BufferCount => Interlocked.Read(ref _buffers);

    /// <summary>Один и тот же звук идёт в два распознавателя: словарный даёт точные команды,
    /// свободный — аргументы («найди погоду») и диктовку. Все вызовы в libvosk под одним замком.</summary>
    public void Attach(Native.Recognizer recognizer, Native.Recognizer? alternate = null)
    {
        lock (_sync)
        {
            _recognizer = recognizer;
            _alternate = alternate;
        }

        BeginPhase();
    }

    /// <summary>Новая фраза: те же распознаватели, но чистая история и очередь.</summary>
    public void BeginPhase()
    {
        lock (_sync)
        {
            _recognizer?.Reset();
            _alternate?.Reset();
            _free.Clear();
            _frames.Clear();
            _partial = string.Empty;
            _freePartial = string.Empty;
        }

        ClearPhase();
    }

    private void ClearPhase()
    {
        while (_utterances.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _lastSpeechTicks, Environment.TickCount64);
    }

    public void Start()
    {
        Exception? last = null;
        foreach (var rate in new[] { _targetRate, 44100, 48000, 32000 })
        {
            try
            {
                _mic.WaveFormat = new WaveFormat(rate, 16, 1);
                CaptureRate = rate;
                _mic.StartRecording();
                return;
            }
            catch (Exception e)
            {
                last = e;
            }
        }

        throw new InvalidOperationException($"Микрофон не запущен: {last?.Message}");
    }

    public void Stop() => _mic.StopRecording();

    public string Partial
    {
        get { lock (_sync) return _partial; }
    }

    /// <summary>Черновик свободной модели: нужен, если слово «джарвис» не попало в словарь.</summary>
    public string FreePartial
    {
        get { lock (_sync) return _freePartial; }
    }

    /// <summary>Всё, что свободная модель услышала за текущую фразу.</summary>
    public string FreeText
    {
        get { lock (_sync) return _free.ToString().Trim(); }
    }

    public bool SpeechInLast(int ms) => Environment.TickCount64 - Interlocked.Read(ref _lastSpeechTicks) < ms;

    /// <summary>Захват с указанной минуты: нейросетевой распознаватель слышит фразу целиком,
    /// а не по границам словарного Vosk.</summary>
    public short[] CaptureSince(long tick)
    {
        lock (_sync)
        {
            var total = 0;
            foreach (var (end, frame) in _frames)
                if (end >= tick) total += frame.Length;

            var result = new short[total];
            var offset = 0;
            foreach (var (end, frame) in _frames)
            {
                if (end < tick) continue;
                frame.CopyTo(result, offset);
                offset += frame.Length;
            }

            return result;
        }
    }

    public bool TryTakeUtterance(out string text) => _utterances.TryDequeue(out text!);

    public string Flush()
    {
        lock (_sync)
        {
            if (_alternate is not null) _free.Append(_alternate.Flush()).Append(' ');
            return _recognizer?.Flush() ?? string.Empty;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (e.BytesRecorded == 0) return;

            var samples = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded)).ToArray();
            if (CaptureRate != _targetRate) samples = Pcm.Resample(samples, CaptureRate, _targetRate);

            string text;
            string partial;
            string freePartial;
            bool endpoint;
            lock (_sync)
            {
                var recognizer = _recognizer;
                if (recognizer is null) return;
                Interlocked.Increment(ref _buffers);
                endpoint = recognizer.Accept(samples, samples.Length);
                text = endpoint ? recognizer.Result() : string.Empty;
                partial = endpoint ? string.Empty : recognizer.Partial();
                _partial = partial;

                if (_alternate is not null)
                {
                    if (_alternate.Accept(samples, samples.Length)) _free.Append(_alternate.Result()).Append(' ');
                    freePartial = _alternate.Partial();
                }
                else
                {
                    freePartial = string.Empty;
                }

                _freePartial = freePartial;

                var endTick = Environment.TickCount64;
                _frames.Add((endTick, samples));
                while (_frames.Count > 0 && _frames[0].EndTick < endTick - CaptureHorizonMs) _frames.RemoveAt(0);
            }

            if (text.Length > 0) _utterances.Enqueue(text);
            if (text.Length > 0 || partial.Length > 0 || freePartial.Length > 0)
                Interlocked.Exchange(ref _lastSpeechTicks, Environment.TickCount64);
        }
        // Поток захвата не имеет права ронять процесс: пропущенный буфер безопаснее молчания помощника.
        catch (Exception ex)
        {
            Log.Line($"mic: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _mic.DataAvailable -= OnData;
        try { _mic.StopRecording(); } catch (Exception) { }
        _mic.Dispose();
        // Распознаватели освобождаются снаружи, поэтому обнуляем ссылки, пока поток захвата гаснет.
        lock (_sync)
        {
            _recognizer = null;
            _alternate = null;
        }
    }
}

internal static class Microphones
{
    /// <summary>Основной микрофон Windows может молчать (USB-адаптер без капсулы, выключенный
    /// кнопкой), поэтому слушаем каждое устройство и берём то, где реально есть сигнал.</summary>
    public static int PickWorking(int sampleRate)
    {
        var fallback = PickDefault();
        if (WaveInEvent.DeviceCount == 0) return -1;

        var levels = new double[WaveInEvent.DeviceCount];
        for (var i = 0; i < WaveInEvent.DeviceCount && i < 6; i++)
        {
            levels[i] = Measure(i, sampleRate);
            Console.WriteLine($"  [{i}] {WaveInEvent.GetCapabilities(i).ProductName}: шум {levels[i]:F0}");
        }

        if (fallback >= 0 && fallback < levels.Length && levels[fallback] >= 10) return fallback;

        var best = fallback;
        for (var i = 0; i < levels.Length; i++)
            if (best < 0 || levels[i] > levels[best]) best = i;

        if (best < 0 || levels[best] < 5)
        {
            Console.WriteLine("  все микрофоны молчат. Скажите что-нибудь и проверьте: Jarvis.exe --record 5");
            return fallback;
        }

        Console.WriteLine($"  основной микрофон [{fallback}] молчит, беру [{best}].");
        return best;
    }

    internal static double Measure(int device, int sampleRate)
    {
        long sum = 0;
        var count = 0;
        try
        {
            using var wave = new WaveInEvent
            {
                DeviceNumber = device,
                WaveFormat = new WaveFormat(sampleRate, 16, 1),
                BufferMilliseconds = 50
            };
            wave.DataAvailable += (_, e) =>
            {
                foreach (var sample in MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded)))
                {
                    sum += (long)sample * sample;
                    count++;
                }
            };
            wave.StartRecording();
            Thread.Sleep(400);
            wave.StopRecording();
            Thread.Sleep(80);
        }
        catch (Exception)
        {
            return 0;
        }

        return count == 0 ? 0 : Math.Sqrt(sum / (double)count);
    }

    public static void Print()
    {
        string? defaultName = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            defaultName = endpoint.FriendlyName;
        }
        catch (Exception)
        {
            // Основной микрофон не определился — печатаем список без отметки.
        }

        Console.WriteLine($"Микрофонов: {WaveInEvent.DeviceCount}");
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            var mark = defaultName is not null && defaultName.StartsWith(caps.ProductName, StringComparison.OrdinalIgnoreCase) ? "  <- основной" : string.Empty;
            Console.WriteLine($"  [{i}] {caps.ProductName} (каналов {caps.Channels}){mark}");
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            Console.WriteLine("Уровни записи:");
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    var volume = device.AudioEndpointVolume;
                    Console.WriteLine($"  {device.FriendlyName}: {(volume.Mute ? "ЗАГЛУШЕН" : $"{volume.MasterVolumeLevelScalar * 100:F0}%")}");
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"  уровни записи недоступны: {e.Message}");
        }
    }

    /// <summary>Индекс WinMM не обязан совпадать с основным устройством записи Windows,
    /// поэтому берём тот, чьё имя совпадает с основным Core Audio-микрофоном.</summary>
    public static int PickDefault()
    {
        if (WaveInEvent.DeviceCount == 0) return -1;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            var name = endpoint.FriendlyName;
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
                if (name.StartsWith(WaveInEvent.GetCapabilities(i).ProductName, StringComparison.OrdinalIgnoreCase))
                    return i;
        }
        catch (Exception)
        {
            // Основной микрофон не определился — берём первый.
        }

        return 0;
    }
}
