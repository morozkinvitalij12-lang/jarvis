using System.Collections.Concurrent;
using System.Text;

namespace Jarvis;

internal sealed class Voice : IDisposable
{
    private readonly BlockingCollection<string?> _queue = new();
    private readonly object _idle = new();
    private readonly Config _cfg;
    private readonly ITts _tts;
    private int _pending;
    private Thread? _worker;

    public Voice(Config cfg)
    {
        _cfg = cfg;
        _tts = Open(cfg);
    }

    public bool Muted { get; set; }

    public string VoiceName => _tts.Name;

    public bool Idle
    {
        get { lock (_idle) return _pending == 0; }
    }

    private float Gain => Math.Clamp(_cfg.Volume, 0, 100) / 100f;

    private static ITts Open(Config cfg)
    {
        if (!cfg.NeuralTts) return new SapiTts(cfg.VoiceCulture, cfg.SpeechRate);
        try
        {
            var voices = PiperTts.InstalledVoices(cfg.PiperVoicesPath).ToList();
            if (voices.Count == 0) throw new DirectoryNotFoundException("нет голосов в " + cfg.PiperVoicesPath);
            var chosen = voices.FirstOrDefault(v => v.Contains(cfg.PiperVoice, StringComparison.OrdinalIgnoreCase)) ?? voices[0];
            return new PiperTts(Path.Combine(cfg.PiperVoicesPath, chosen), cfg.EspeakDataPath, cfg.PiperSpeed, cfg.PiperThreads);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[tts] нейросетевой голос недоступен ({e.Message}), беру системный SAPI");
            return new SapiTts(cfg.VoiceCulture, cfg.SpeechRate);
        }
    }

    public void Start()
    {
        _worker = new Thread(Loop) { IsBackground = true, Name = "jarvis-tts" };
        _worker.Start();
    }

    public void Say(string text)
    {
        if (Muted || Guard.DryRun || string.IsNullOrWhiteSpace(text)) return;
        lock (_idle) _pending++;
        _queue.Add(text);
    }

    public void StopSpeaking()
    {
        while (_queue.TryTake(out _)) { }
        lock (_idle) _pending = 0;
        Monitor.PulseAll(_idle);
        _tts.Stop();
    }

    public bool WaitUntilIdle(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (_idle)
        {
            while (_pending > 0)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return false;
                Monitor.Wait(_idle, (int)remaining);
            }

            return true;
        }
    }

    private void Loop()
    {
        foreach (var phrase in _queue.GetConsumingEnumerable())
        {
            if (phrase is null) break;
            try
            {
                foreach (var sentence in Split(phrase))
                {
                    try
                    {
                        _tts.Speak(sentence, Gain);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"[tts] {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Line($"очередь озвучки: {e}");
            }
            finally
            {
                lock (_idle)
                {
                    _pending = Math.Max(0, _pending - 1);
                    Monitor.PulseAll(_idle);
                }
            }
        }
    }

    private static IEnumerable<string> Split(string text)
    {
        var buffer = new StringBuilder();
        foreach (var ch in text)
        {
            buffer.Append(ch);
            if (ch is '.' or '!' or '?' or ';')
            {
                yield return buffer.ToString().Trim();
                buffer.Clear();
            }
        }

        if (buffer.ToString().Trim().Length > 0) yield return buffer.ToString().Trim();
    }

    /// <summary>Типовые реплики озвучиваются заранее и остаются в памяти, поэтому первый ответ звучит сразу.</summary>
    public void Prewarm(IEnumerable<string> phrases)
    {
        var list = phrases.SelectMany(Split).Distinct(StringComparer.Ordinal).ToList();
        new Thread(() =>
        {
            foreach (var sentence in list)
                try { _ = _tts.Render(sentence); } catch (Exception) { }
        })
        { IsBackground = true, Name = "jarvis-prewarm" }.Start();
    }

    public void SpeakToWave(string text, string wavPath, float gain = 1f)
    {
        var pcm = _tts.Render(text);
        if (pcm.Length == 0) throw new InvalidOperationException("Голос не выдал звук.");
        if (gain != 1f)
        {
            var scaled = new short[pcm.Length];
            for (var i = 0; i < pcm.Length; i++)
                scaled[i] = (short)Math.Clamp((int)(pcm[i] * gain), short.MinValue, short.MaxValue);
            pcm = scaled;
        }

        Wav.Write(Path.GetFullPath(wavPath), pcm, _tts.SampleRate);
    }

    public void Dispose()
    {
        _queue.Add(null);
        // Дожидаться потока нужно до Dispose движка: иначе Speak продолжится на освобождённом TTS.
        _worker?.Join(TimeSpan.FromSeconds(10));
        _tts.Dispose();
    }
}
