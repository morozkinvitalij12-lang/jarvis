using System.Text.Json;
using PiperPhonemize;
using SherpaOnnx;

namespace Jarvis;

/// <summary>Нейросетевой русский голос (Piper поверх sherpa-onnx), полностью офлайн, в этом же процессе.</summary>
internal sealed class PiperTts : ITts
{
    private readonly OfflineTts _tts;
    private readonly HashSet<char> _phonemes;
    private readonly Speaker _speaker = new();
    private readonly double _speed;
    private readonly Dictionary<string, short[]> _cache = new(StringComparer.Ordinal);

    public string Name { get; }
    public int SampleRate { get; }

    public PiperTts(string voiceDir, string espeakData, double speed, int threads = 2)
    {
        Name = Path.GetFileName(voiceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var model = Path.Combine(voiceDir, Name + ".onnx");
        var meta = model + ".json";
        var tokens = Path.Combine(voiceDir, "tokens.txt");
        if (!File.Exists(model)) throw new FileNotFoundException("Нет модели голоса", model);
        if (!File.Exists(meta)) throw new FileNotFoundException("Нет описания голоса", meta);
        if (!Directory.Exists(espeakData)) throw new DirectoryNotFoundException("Нет каталога espeak-ng-data: " + espeakData);

        _phonemes = new HashSet<char>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(meta)))
            foreach (var phoneme in doc.RootElement.GetProperty("phoneme_id_map").EnumerateObject())
                if (phoneme.Name.Length == 1)
                    _phonemes.Add(phoneme.Name[0]);
        if (_phonemes.Count == 0) throw new InvalidDataException("Пустая таблица фонем в " + meta);

        InitPhonemizer(espeakData);

        var config = new OfflineTtsConfig
        {
            Model = new OfflineTtsModelConfig
            {
                Vits = new OfflineTtsVitsModelConfig
                {
                    Model = model,
                    Tokens = File.Exists(tokens) ? tokens : meta,
                    DataDir = string.Empty,
                    Lexicon = string.Empty,
                    DictDir = string.Empty
                },
                NumThreads = threads,
                Debug = 0,
                Provider = "cpu"
            },
            MaxNumSentences = 3
        };
        _tts = new OfflineTts(config);
        SampleRate = _tts.SampleRate;
        _speed = speed;
        if (SampleRate <= 0) throw new InvalidOperationException("sherpa-onnx не вернул частоту голоса.");
    }

    public static IEnumerable<string> InstalledVoices(string root)
    {
        if (!Directory.Exists(root)) return [];
        var found = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (File.Exists(Path.Combine(directory, name + ".onnx"))) found.Add(name);
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    private static readonly object Gate = new();
    private static string? _espeakData;

    /// <summary>Фонемизатор инициализируется один раз на процесс: повторный вызов при
    /// создании второго голоса (панель «Примеры голосов») ломает уже поднятый espeak-ng.</summary>
    private static void InitPhonemizer(string espeakData)
    {
        lock (Gate)
        {
            if (_espeakData == espeakData) return;
            if (_espeakData is null && PiperPhonemizeApi.Initialize(espeakData) < 0)
                throw new InvalidOperationException("Фонемизатор не поднялся.");
            _espeakData = espeakData;
        }
    }

    /// <summary>Фонемизатор и sherpa-onnx не потокобезопасны: весь рендер идёт под общим замком,
    /// иначе предпрогрев, ответ помощника и запись примеров голосов падают в нативный код одновременно.</summary>
    public short[] Render(string text)
    {
        lock (Gate)
        {
            if (_cache.TryGetValue(text, out var cached)) return cached;

            using var result = PiperPhonemizeApi.Text(text, "ru");
            var sentences = new int[result.NumSentences][];
            var space = _phonemes.Contains(' ');
            for (var s = 0; s < result.NumSentences; s++)
            {
                var ids = new List<int>();
                foreach (var word in result.GetPhonemesAsString(s).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (ids.Count > 0 && space) ids.Add(' ');
                    foreach (var ch in word)
                        if (_phonemes.Contains(ch))
                            ids.Add(ch);
                }

                sentences[s] = ids.ToArray();
            }

            if (sentences.All(s => s.Length == 0)) return [];

            var generation = new OfflineTtsGenerationConfig
            {
                Speed = (float)Math.Clamp(_speed, 0.5, 2.0),
                Sid = 0,
                PhonemeCodepoints = sentences
            };
            var audio = _tts.GenerateWithConfig(string.Empty, generation, null);
            short[] pcm;
            try
            {
                var samples = audio.Samples;
                pcm = new short[samples.Length];
                for (var i = 0; i < samples.Length; i++)
                    pcm[i] = (short)Math.Clamp((int)(samples[i] * 32767f), short.MinValue, short.MaxValue);
            }
            finally
            {
                audio.Dispose();
            }

            if (pcm.Length > 0 && _cache.Count < 64) _cache[text] = pcm;
            return pcm;
        }
    }

    public void Speak(string text, float gain)
    {
        var pcm = Render(text);
        if (pcm.Length == 0) return;
        _speaker.Play(pcm, SampleRate, gain);
    }

    public void Stop() => _speaker.Stop();

    public void Dispose()
    {
        lock (Gate)
        {
            _speaker.Dispose();
            _tts.Dispose();
        }
    }
}
