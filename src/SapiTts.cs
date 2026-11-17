using System.Speech.Synthesis;

namespace Jarvis;

/// <summary>Запасной голос: системный SAPI. Используется, если нейросетевая модель не скачана.</summary>
internal sealed class SapiTts : ITts
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly Dictionary<string, short[]> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly int _rate;
    private int _sampleRate;

    public SapiTts(string culture, int rate)
    {
        _rate = rate;
        _synth.Rate = rate;
        var voices = _synth.GetInstalledVoices().Where(v => v.Enabled).ToList();
        var preferred = voices.FirstOrDefault(v => v.VoiceInfo.Culture.Name.Equals(culture, StringComparison.OrdinalIgnoreCase))
                        ?? voices.FirstOrDefault(v => v.VoiceInfo.Culture.TwoLetterISOLanguageName == "ru");
        if (preferred is null)
        {
            Name = "(нет голоса)";
            return;
        }

        _synth.SelectVoice(preferred.VoiceInfo.Name);
        Name = preferred.VoiceInfo.Name + " (SAPI)";
    }

    public string Name { get; }

    public int SampleRate => _sampleRate == 0 ? 22050 : _sampleRate;

    /// <summary>SpeechSynthesizer один и не переживает одновременные вызовы: прогрев и ответ
    /// идут в разных потоках, поэтому весь синтез сериализуется.</summary>
    public short[] Render(string text)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(text, out var cached)) return cached;

            var temp = Path.Combine(Path.GetTempPath(), $"jarvis-sapi-{Guid.NewGuid():N}.wav");
            _synth.SetOutputToWaveFile(temp);
            try
            {
                _synth.Speak(text);
                if (!File.Exists(temp)) return [];
                var pcm = Pcm.ReadWav(temp, out var rate);
                _sampleRate = rate;
                if (_cache.Count < 64) _cache[text] = pcm;
                return pcm;
            }
            finally
            {
                try
                {
                    _synth.SetOutputToNull();
                    File.Delete(temp);
                }
                catch (Exception)
                {
                    // Временный WAV может быть ещё занят синтезатором — это не повод ронять озвучку.
                }
            }
        }
    }

    public void Speak(string text, float gain)
    {
        lock (_gate)
        {
            _synth.Volume = (int)Math.Clamp(gain * 100, 0, 100);
            _synth.Rate = _rate;
            _synth.SetOutputToDefaultAudioDevice();
            _synth.Speak(text);
        }
    }

    public void Stop()
    {
        // Остановка не ждёт очередь: поток речи может держать замок до конца фразы.
        if (!Monitor.TryEnter(_gate, 200)) return;
        try
        {
            _synth.SpeakAsyncCancelAll();
            _synth.SetOutputToNull();
            _synth.SetOutputToDefaultAudioDevice();
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public void Dispose()
    {
        Stop();
        _synth.Dispose();
    }
}
