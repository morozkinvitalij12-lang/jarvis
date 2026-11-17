using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis;

internal static class Native
{
    private const string Lib = "libvosk";

    /// <summary>Vosk ждёт JSON в UTF-8, поэтому кириллицу не экранируем.</summary>
    private static readonly JsonSerializerOptions GrammarJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void vosk_set_log_level(int level);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vosk_model_new([MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void vosk_model_free(IntPtr model);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vosk_recognizer_new(IntPtr model, float sampleRate);

    [DllImport(Lib, EntryPoint = "vosk_recognizer_new_grm", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vosk_recognizer_new_grm(IntPtr model, float sampleRate, [MarshalAs(UnmanagedType.LPUTF8Str)] string grammar);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void vosk_recognizer_free(IntPtr recognizer);

    [DllImport(Lib, EntryPoint = "vosk_recognizer_accept_waveform_s", CallingConvention = CallingConvention.Cdecl)]
    private static extern int vosk_recognizer_accept_waveform(IntPtr recognizer, short[] data, int samples);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vosk_recognizer_result(IntPtr recognizer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vosk_recognizer_partial_result(IntPtr recognizer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vosk_recognizer_final_result(IntPtr recognizer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void vosk_recognizer_reset(IntPtr recognizer);

    public static void Init()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
        vosk_set_log_level(-1);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Lib) return IntPtr.Zero;
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "native") })
        {
            var candidate = Path.Combine(dir, "libvosk.dll");
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        }
        return NativeLibrary.Load("libvosk.dll", assembly, searchPath);
    }

    public sealed class Model : IDisposable
    {
        private IntPtr _handle;

        public Model(string path)
        {
            _handle = vosk_model_new(path);
            if (_handle == IntPtr.Zero) throw new InvalidOperationException($"Vosk не смог загрузить модель: {path}");
        }

        public Recognizer CreateRecognizer(float sampleRate) => new(_handle, sampleRate);

        /// <summary>Распознаватель с ограниченным словарём: Vosk собирает фразы только из
        /// переданных слов, поэтому посторонняя речь не превращается в случайные команды.</summary>
        public Recognizer? CreateGrammarRecognizer(float sampleRate, IReadOnlyList<string> words)
        {
            if (_handle == IntPtr.Zero || words.Count == 0) return null;
            var grammar = JsonSerializer.Serialize(words, GrammarJson);
            var handle = vosk_recognizer_new_grm(_handle, sampleRate, grammar);
            return handle == IntPtr.Zero ? null : new Recognizer(handle);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) vosk_model_free(_handle);
            _handle = IntPtr.Zero;
        }
    }

    public sealed class Recognizer : IDisposable
    {
        private IntPtr _handle;

        internal Recognizer(IntPtr model, float sampleRate)
        {
            _handle = vosk_recognizer_new(model, sampleRate);
            if (_handle == IntPtr.Zero) throw new InvalidOperationException("Не удалось создать Vosk recognizer.");
        }

        internal Recognizer(IntPtr handle) => _handle = handle;

        public bool Accept(short[] samples, int count) => _handle != IntPtr.Zero && vosk_recognizer_accept_waveform(_handle, samples, count) == 1;

        public string Result() => Read(_handle == IntPtr.Zero ? IntPtr.Zero : vosk_recognizer_result(_handle));

        public string Partial() => Read(_handle == IntPtr.Zero ? IntPtr.Zero : vosk_recognizer_partial_result(_handle));

        public string Flush() => Read(_handle == IntPtr.Zero ? IntPtr.Zero : vosk_recognizer_final_result(_handle));

        /// <summary>Сброс без пересоздания: recognizer остаётся тем же объектом, поэтому его не освобождают во время работы потока микрофона.</summary>
        public void Reset()
        {
            if (_handle != IntPtr.Zero) vosk_recognizer_reset(_handle);
        }

        public string ResultRaw() => Text(_handle == IntPtr.Zero ? IntPtr.Zero : vosk_recognizer_result(_handle));

        public string PartialRaw() => Text(_handle == IntPtr.Zero ? IntPtr.Zero : vosk_recognizer_partial_result(_handle));

        public string FlushRaw() => Text(_handle == IntPtr.Zero ? IntPtr.Zero : vosk_recognizer_final_result(_handle));

        private static string Text(IntPtr ptr) => ptr == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(ptr) ?? string.Empty);

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) vosk_recognizer_free(_handle);
            _handle = IntPtr.Zero;
        }

        private static string Read(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return string.Empty;
            var json = Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
            return Words(json);
        }

        public static string Words(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                foreach (var key in new[] { "text", "partial" })
                    if (root.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String)
                        return Clean(field.GetString() ?? string.Empty);

                if (root.TryGetProperty("result", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var item in results.EnumerateArray())
                    {
                        if (item.TryGetProperty("word", out var word) && word.ValueKind == JsonValueKind.String)
                        {
                            if (sb.Length > 0) sb.Append(' ');
                            sb.Append(word.GetString());
                        }
                    }
                    return Clean(sb.ToString());
                }
            }
            catch (JsonException)
            {
                return string.Empty;
            }
            return string.Empty;
        }

        /// <summary>В режиме словаря всё постороннее Vosk помечает как [unk] — это не текст команды.</summary>
        private static string Clean(string text) =>
            Regex.Replace(text.Replace("[unk]", " ", StringComparison.OrdinalIgnoreCase), "\\s+", " ").Trim();
    }
}
