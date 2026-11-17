using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis;

public sealed class Config
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [JsonPropertyName("wakeWords")] public string[] WakeWords { get; set; } = ["джарвис", "джарвиса", "jarvis"];
    [JsonPropertyName("tts")] public string TtsBackend { get; set; } = "piper";
    [JsonPropertyName("piperVoice")] public string PiperVoice { get; set; } = "ruslan";
    [JsonPropertyName("piperSpeed")] public double PiperSpeed { get; set; } = 1.0;
    [JsonPropertyName("piperThreads")] public int PiperThreads { get; set; } = 2;
    [JsonPropertyName("piperVoicesDir")] public string PiperVoicesDir { get; set; } = Path.Combine("models", "piper", "voices");
    [JsonPropertyName("espeakDataDir")] public string EspeakDataDir { get; set; } = Path.Combine("models", "piper", "espeak-ng-data");
    [JsonPropertyName("modelDir")] public string ModelDir { get; set; } = Path.Combine("models", "vosk-model-small-ru-0.22");
    [JsonPropertyName("neuralAsr")] public bool NeuralAsr { get; set; } = true;
    [JsonPropertyName("neuralAsrDir")] public string NeuralAsrDir { get; set; } = Path.Combine("models", "sherpa-nemo-giga-am-v3");
    [JsonPropertyName("neuralAsrThreads")] public int NeuralAsrThreads { get; set; } = 2;
    [JsonPropertyName("sampleRate")] public int SampleRate { get; set; } = 16000;
    [JsonPropertyName("microphone")] public int Microphone { get; set; } = -1;
    [JsonPropertyName("overlay")] public bool Overlay { get; set; } = true;
    [JsonPropertyName("voiceCulture")] public string VoiceCulture { get; set; } = "ru-RU";
    [JsonPropertyName("speechRate")] public int SpeechRate { get; set; } = 1;
    [JsonPropertyName("volume")] public int Volume { get; set; } = 100;
    [JsonPropertyName("commandTimeoutSeconds")] public int CommandTimeoutSeconds { get; set; } = 12;
    [JsonPropertyName("partialTimeoutSeconds")] public int PartialTimeoutSeconds { get; set; } = 2;
    [JsonPropertyName("dictationDir")] public string DictationDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Jarvis");
    [JsonPropertyName("greeting")] public string Greeting { get; set; } = "Системы запущены. Слушаю вас.";
    [JsonPropertyName("weatherCity")] public string WeatherCity { get; set; } = "Москва";
    [JsonPropertyName("smartAnswers")] public bool SmartAnswers { get; set; } = true;
    [JsonPropertyName("ollamaUrl")] public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
    [JsonPropertyName("ollamaModel")] public string OllamaModel { get; set; } = "qwen3.5:4b";
    [JsonPropertyName("brainTimeoutSeconds")] public int BrainTimeoutSeconds { get; set; } = 120;
    [JsonPropertyName("scenarios")] public Scenario[] Scenarios { get; set; } =
    [
        new() { Name = "утро", Steps = ["который час", "погода", "курсы валют", "открой браузер"] },
        new() { Name = "работа", Steps = ["открой браузер", "открой код", "громкость 20"] },
        new() { Name = "кино", Steps = ["громкость 70", "разверни на весь экран"] },
        new() { Name = "ночь", Steps = ["стоп музыка", "закрой все окна", "режим ожидания"] },
    ];

    public static string Root => AppContext.BaseDirectory;
    [JsonIgnore] public string ModelPath => Path.IsPathRooted(ModelDir) ? ModelDir : Path.Combine(Root, ModelDir);
    [JsonIgnore] public string ConfigPath => Path.Combine(Root, "jarvis.json");
    [JsonIgnore] public bool NeuralTts => !TtsBackend.Equals("sapi", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string PiperVoicesPath => Resolve(PiperVoicesDir);
    [JsonIgnore] public string EspeakDataPath => Resolve(EspeakDataDir);
    [JsonIgnore] public string NeuralAsrPath => Resolve(NeuralAsrDir);

    private static string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(Root, path);

    public static Config Load()
    {
        var path = Path.Combine(Root, "jarvis.json");
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json);
                if (loaded is not null) return loaded;
            }
            catch (JsonException)
            {
                File.Move(path, path + ".broken", overwrite: true);
            }
        }

        var created = new Config();
        created.Save();
        return created;
    }

    public void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Json));
}
