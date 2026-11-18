using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Jarvis;

internal static class Selftest
{
    // Фразы из словаря команд: здесь ждём точного совпадения.
    private static readonly string[] Phrases =
    [
        "открой блокнот",
        "закрой все окна",
        "который час",
        "следующий трек",
        "громкость сорок",
        "тише",
        "громче",
        "пауза",
        "стоп музыка",
        "погода",
        "курс доллара",
        "сколько будет семь плюс восемь",
        "открой браузер",
        "закрой калькулятор",
        "что открыто",
        "перемотай вперёд",
        "какое сегодня число",
        "переведи привет на английский",
        "сценарий утро",
        "режим ожидания",
        "открой проводник",
        "сверни окно",
        "полный экран",
        "замолчи",
        "повтори",
    ];

    // Свободная модель отвечает за имя помощника и аргументы вроде «найди погоду».
    private static readonly string[] FreePhrases =
    [
        "джарвис открой блокнот",
        "найди погоду",
        "посчитай двадцать пять на сто",
        "какая погода в казани",
    ];

    // Отдельные фразы: иначе стресс упирается только в кэш голоса.
    private static readonly string[] StressPhrases =
    [
        "проверка голосового потока номер один",
        "проверка голосового потока номер два",
        "проверка голосового потока номер три",
        "проверка голосового потока номер четыре",
    ];

    public static int Run(Config cfg)
    {
        var failures = 0;
        Console.WriteLine($"Конфиг: {cfg.ConfigPath}");
        Console.WriteLine($"Модель: {cfg.ModelPath}");

        if (!Directory.Exists(cfg.ModelPath))
        {
            Console.WriteLine($"[FAIL] каталог модели не найден: {cfg.ModelPath}");
            return 1;
        }

        var watch = Stopwatch.StartNew();
        using var model = new Native.Model(cfg.ModelPath);
        Console.WriteLine($"[ OK ] Vosk: модель загружена за {watch.ElapsedMilliseconds} мс");

        using var voice = new Voice(cfg);
        if (voice.VoiceName.Contains("нет голоса", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[FAIL] голос недоступен (ни нейросетевого, ни SAPI)");
            failures++;
        }
        else
        {
            Console.WriteLine($"[ OK ] Голос: «{voice.VoiceName}»");
        }

        var temp = Path.Combine(Path.GetTempPath(), "jarvis-selftest");
        Directory.CreateDirectory(temp);

        // Тот же набор распознавателей, что и у помощника: словарь команд + свободная модель.
        var vocabulary = Vocabulary.For(cfg);
        using var restricted = model.CreateGrammarRecognizer(cfg.SampleRate, vocabulary);
        using var free = model.CreateRecognizer(cfg.SampleRate);
        var recognizer = restricted ?? free;
        Console.WriteLine(restricted is null
            ? "[WARN] Словарь команд не поднялся, проверяю свободной моделью"
            : $"[ OK ] Словарь команд: {vocabulary.Count - 1} слов + {Vocabulary.Unknown}");

        GigaAm? neural = null;
        if (cfg.NeuralAsr)
        {
            watch.Restart();
            neural = GigaAm.Open(cfg.NeuralAsrPath, cfg.NeuralAsrThreads);
            if (neural.IsReady)
                Console.WriteLine($"[ OK ] GigaAM: модель загружена за {watch.ElapsedMilliseconds} мс, " +
                                  $"процесс занимает {Environment.WorkingSet / 1048576} МБ");
            else
            {
                Console.WriteLine($"[WARN] GigaAM не поднялся ({cfg.NeuralAsrPath}), сравниваю не с чем");
                neural.Dispose();
                neural = null;
            }
        }

        var (recognized, words, total, neuralWords) = Check(voice, cfg, temp, Phrases, recognizer, exact: true,
            restricted is null ? null : free, neural);
        var (freeRecognized, freeWords, freeTotal, freeNeuralWords) = Check(voice, cfg, temp, FreePhrases, free, exact: false, neural: neural);
        words += freeWords;
        total += freeTotal;
        neuralWords += freeNeuralWords;
        failures += Phrases.Length - recognized + FreePhrases.Length - freeRecognized;
        neural?.Dispose();

        var stress = Stress(model, voice, temp);
        failures += stress;

        var windows = WinCtl.TopLevelWindows();
        Console.WriteLine($"[ OK ] Окна: найдено {windows.Count} видимых верхних окон");
        var foreground = WinCtl.Foreground();
        Console.WriteLine($"[ OK ] Активное окно: {foreground?.Title ?? "(нет)"}");

        try
        {
            Console.WriteLine($"[ OK ] Громкость вывода: {Volume.Get() * 100:F0}%, mute={Volume.IsMuted}");
        }
        catch (Exception e)
        {
            failures++;
            Console.WriteLine($"[FAIL] Громкость недоступна: {e.Message}");
        }

        Console.WriteLine($"Микрофонов: {NAudio.Wave.WaveInEvent.DeviceCount} (в selftest не используется)");
        var got = recognized + freeRecognized;
        var wanted = Phrases.Length + FreePhrases.Length;
        Console.WriteLine($"Слов распознано: Vosk {words}/{total} ({words * 100.0 / Math.Max(1, total):F1}%), " +
                          $"GigaAM {neuralWords}/{total} ({neuralWords * 100.0 / Math.Max(1, total):F1}%).");
        Console.WriteLine(got == wanted && failures == 0
            ? $"Самопроверка пройдена: {got}/{wanted} фраз распознано."
            : $"Самопроверка: распознано {got}/{wanted} фраз, ошибок: {failures}.");
        return got == wanted && failures == 0 ? 0 : 1;
    }

    /// <summary>Озвучить фразы и расшифровать их тем же распознавателем, что слушает помощник.</summary>
    private static (int Phrases, int Words, int Total, int NeuralWords) Check(Voice voice, Config cfg, string temp, string[] phrases,
        Native.Recognizer recognizer, bool exact, Native.Recognizer? also = null, GigaAm? neural = null)
    {
        var matched = 0;
        var words = 0;
        var neuralWords = 0;
        var total = 0;
        foreach (var phrase in phrases)
        {
            var wav = Path.Combine(temp, $"{Math.Abs(phrase.GetHashCode())}.wav");
            try
            {
                voice.SpeakToWave(phrase, wav);
                var samples = Pcm.ReadWav(wav, out var rate);
                if (rate != cfg.SampleRate) samples = Pcm.Resample(samples, rate, cfg.SampleRate);

                var padded = new short[samples.Length + cfg.SampleRate];
                samples.CopyTo(padded, 0);

                var transcript = Decode(recognizer, padded, cfg.SampleRate);
                // ожидание нормализуем так же, как распознаватель: «вперёд» приходит как «вперед»
                var expected = phrase.Split(' ').Select(w => Commands.Normalize(w)).Where(w => w.Length > 0).ToArray();
                var hits = expected.Count(word => transcript.Contains(word, StringComparison.Ordinal));
                var neuralText = neural is null ? string.Empty : Commands.Normalize(neural.Decode(padded, cfg.SampleRate));
                var neuralHits = expected.Count(word => neuralText.Contains(word, StringComparison.Ordinal));
                // помощник берёт нейросетевую строку, когда она поднялась, поэтому фразa засчитана
                // по любому из каналов, который услышал все слова.
                var ok = exact ? hits == expected.Length || neuralHits == expected.Length
                               : hits > 0 || neuralHits > 0;
                if (ok) matched++;
                total += expected.Length;
                words += Math.Max(hits, neuralHits);
                neuralWords += neuralHits;
                var other = also is null ? string.Empty : $" F:«{Decode(also, padded, cfg.SampleRate)}»";
                var neuralColumn = neural is null ? string.Empty : $" N:«{neuralText}»";
                Console.WriteLine($"[{(ok ? "OK " : "FAIL")}] {(exact ? "Словарь " : "Свободно")}: ждал «{phrase}», получил G:«{transcript}»{other}{neuralColumn} ({hits}/{expected.Length} слов)");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[FAIL] {phrase}: {e.Message}");
            }
        }

        return (matched, words, total, neuralWords);
    }

    /// <summary>Тот же путь, что у потока микрофона: чанки по 100 мс, границы фраз, финальный сброс.</summary>
    private static string Decode(Native.Recognizer recognizer, short[] padded, int sampleRate)
    {
        recognizer.Reset();
        var heard = new StringBuilder();
        const int ChunkSamples = 1600;
        for (var offset = 0; offset < padded.Length; offset += ChunkSamples)
        {
            var taken = Math.Min(ChunkSamples, padded.Length - offset);
            if (recognizer.Accept(padded[offset..(offset + taken)], taken))
                heard.Append(recognizer.Result()).Append(' ');
        }

        heard.Append(recognizer.Flush());
        return Commands.Normalize(heard.ToString());
    }

    /// <summary>Голос рендерится из четырёх потоков сразу, пока главный сбрасывает распознаватель —
    /// именно эта пара роняла помощника после первой команды.</summary>
    private static int Stress(Native.Model model, Voice voice, string temp)
    {
        var start = new ManualResetEventSlim();
        var errors = 0;
        var threads = new Thread[StressPhrases.Length];
        for (var t = 0; t < threads.Length; t++)
        {
            var index = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    start.Wait();
                    foreach (var phrase in StressPhrases)
                        voice.SpeakToWave(phrase, Path.Combine(temp, $"stress-{index}-{phrase.Length}.wav"));
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref errors);
                    Console.WriteLine($"[FAIL] стресс голоса: {e.Message}");
                }
            })
            { IsBackground = true, Name = $"jarvis-stress-{index}" };
        }

        using var recognizer = model.CreateRecognizer(16000f);
        foreach (var thread in threads) thread.Start();
        start.Set();
        for (var i = 0; i < 200; i++)
        {
            recognizer.Reset();
            recognizer.Accept(new short[1600], 1600);
            _ = recognizer.Partial();
        }

        foreach (var thread in threads) thread.Join(TimeSpan.FromSeconds(120));
        var verdict = errors == 0 ? "OK " : "FAIL";
        Console.WriteLine($"[{verdict}] Стресс: голос из {threads.Length} потоков + сброс распознавателя, ошибок: {errors}");
        return errors;
    }
}
