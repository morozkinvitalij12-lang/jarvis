using SherpaOnnx;

namespace Jarvis;

/// <summary>Русская распознаватель GigaAM v3 поверх того же sherpa-onnx, что и голоса помощника.
/// Модель не потоковая: фразу декодируем целиком, зато слышит она заметно лучше словарьной Vosk.</summary>
internal sealed class GigaAm : IDisposable
{
    private readonly OfflineRecognizer? _recognizer;
    private readonly object _sync = new();

    private GigaAm(OfflineRecognizer? recognizer) => _recognizer = recognizer;

    public bool IsReady => _recognizer is not null;

    /// <summary>Молча возвращает нерабочий экземпляр, если файлов или памяти не хватило:
    /// помощник обязан остаться слышащим через Vosk.</summary>
    public static GigaAm Open(string dir, int threads)
    {
        var model = Path.Combine(dir, "model.int8.onnx");
        if (!File.Exists(model)) model = Path.Combine(dir, "model.onnx");
        var tokens = Path.Combine(dir, "tokens.txt");
        if (!File.Exists(model) || !File.Exists(tokens)) return new GigaAm(null);

        try
        {
            var config = new OfflineRecognizerConfig
            {
                ModelConfig = new OfflineModelConfig
                {
                    NeMoCtc = new OfflineNemoEncDecCtcModelConfig { Model = model },
                    Tokens = tokens,
                    NumThreads = Math.Clamp(threads, 1, 8),
                    Debug = 0,
                    Provider = "cpu"
                },
                DecodingMethod = "greedy_search"
            };
            return new GigaAm(new OfflineRecognizer(config));
        }
        catch (Exception e)
        {
            Log.Line($"gigaam: {e.Message}");
            return new GigaAm(null);
        }
    }

    /// <summary>Целиком декодирует отрезок звука. Пусто — если модель не поднялась или отрезок короче полсекунды.</summary>
    public string Decode(short[] samples, int sampleRate)
    {
        var recognizer = _recognizer;
        if (recognizer is null || samples.Length < sampleRate / 2) return string.Empty;

        var floats = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) floats[i] = samples[i] / 32768f;

        lock (_sync)
        {
            try
            {
                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(sampleRate, floats);
                recognizer.Decode(stream);
                return stream.Result.Text.Trim();
            }
            catch (Exception e)
            {
                Log.Line($"gigaam decode: {e.Message}");
                return string.Empty;
            }
        }
    }

    public void Dispose() => _recognizer?.Dispose();
}
