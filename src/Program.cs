using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

[assembly: SupportedOSPlatform("windows10.0.19041")]

namespace Jarvis;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Line($"сбой потока: {e.ExceptionObject}");
        Native.Init();
        if (Environment.GetEnvironmentVariable("JARVIS_DRY") == "1") Guard.DryRun = true;
        var cfg = Config.Load();
        if (args.Length == 0) return Gui(cfg);

        switch (args[0])
        {
            case "--console":
                return RunConsole(cfg);
            case "--selftest":
                return Selftest.Run(cfg);
            case "--say":
                return Say(cfg, Value(args, "--say"));
            case "--apps":
                // С проверкой по имени: что помощник найдёт, если сказать «открой <имя>».
                var needle = args.Length > 1 ? Commands.Normalize(string.Join(' ', args[1..])) : string.Empty;
                if (needle.Length > 0)
                {
                    if (AppsIndex.Resolve(needle) is not { } found)
                    {
                        Console.WriteLine($"«{needle}» — не найдено");
                        return 1;
                    }

                    Console.WriteLine($"{needle} -> {found.File} {found.Arguments}".TrimEnd());
                    return 0;
                }

                foreach (var hit in AppsIndex.All().OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"{hit.Name} -> {hit.File} {hit.Arguments}".TrimEnd());
                return 0;
            case "--listen":
                return Listen(cfg, args);
            case "--record":
                return Record(cfg, args);
            case "--wav":
                return WavMode(cfg, args);
            case "--transcribe":
                return Transcribe(cfg, Value(args, "--transcribe"));
            case "--mics":
                Microphones.Print();
                return 0;
            case "--mic":
                return MicMode(cfg, args);
            case "--ui":
                using (var panel = new Shell(cfg, null, true)) panel.Run();
                return 0;
            case "--render":
                return RenderPanel(cfg, args);
            case "--voices":
                return Voices(cfg, args);
            case "--windows":
                foreach (var window in WinCtl.TopLevelWindows())
                    Console.WriteLine($"[{window.Pid}] {window.Title}");
                return 0;
            case "--plan":
                return Plan(cfg, args);
            // Разведка ответов сервисов: тот же код, что идёт за голосовой командой,
            // только без синтеза и без микрофона.
            case "--weather":
                Console.WriteLine(Weather.Report(args.Length > 1 ? string.Join(' ', args[1..]) : string.Empty, cfg.WeatherCity)
                                  ?? "сервис погоды не ответил");
                return 0;
            case "--course":
                Console.WriteLine(Money.Report(args.Length > 1 ? string.Join(' ', args[1..]) : string.Empty) ?? "Центробанк не ответил");
                return 0;
            case "--translate":
                Console.WriteLine(Translate.To(args.Length > 1 ? string.Join(' ', args[1..]) : string.Empty) ?? "перевод недоступен");
                return 0;
            case "--screen":
                return ScreenMode(args);
            case "--ocr":
                return OcrMode(args);
            case "--calc":
                Console.WriteLine(Calc.Evaluate(args.Length > 1 ? string.Join(' ', args[1..]) : string.Empty) ?? "не понял пример");
                return 0;
            case "--scenario":
                foreach (var scenario in cfg.Scenarios)
                    Console.WriteLine($"{scenario.Name}: {string.Join(" → ", scenario.Steps)}");
                return 0;
            case "--sites":
                foreach (var site in Sites.All(cfg.Sites))
                    Console.WriteLine($"{site.Key} → {site.Value}");
                return 0;
            case "--brain":
                return BrainMode(cfg, args);
            case "--volume":
                return VolumeMode(cfg, args);
            case "--help":
            default:
                Help();
                return args[0] == "--help" ? 0 : 1;
        }
    }

    private static string Value(string[] args, string flag)
    {
        var index = Array.FindIndex(args, a => a == flag);
        return index < 0 || index + 1 >= args.Length ? string.Empty : args[index + 1];
    }

    /// <summary>Запуск двойным щелчком: вместо чёрного окна — панель с вкладками и трей.
    /// Консоль отвязывается, её вывод переезжает во вкладку «Пульт».</summary>
    private static int Gui(Config cfg)
    {
        var assistant = new Assistant(cfg);
        ConsoleHub.Attach(TextWriter.Null);
        FreeConsole();

        using var shell = new Shell(cfg, assistant, cfg.Overlay);
        var worker = new Thread(() =>
        {
            try
            {
                assistant.Run();
            }
            catch (Exception e)
            {
                Log.Line($"сессия: {e}");
                Console.WriteLine($"JARVIS: сбой — {e.Message}");
            }

            shell.OnAssistantStopped();
        })
        { IsBackground = true, Name = "jarvis-core" };

        worker.Start();
        shell.Run();
        assistant.Quit();
        worker.Join(TimeSpan.FromSeconds(6));
        return 0;
    }

    /// <summary>Старый режим: помощник живёт в обычном консольном окне, без панели и трея.</summary>
    private static int RunConsole(Config cfg)
    {
        try
        {
            return new Assistant(cfg).Run();
        }
        catch (Exception e)
        {
            Log.Line($"сессия: {e}");
            Console.WriteLine($"JARVIS: сбой — {e.Message}");
            return 1;
        }
    }

    private static int Say(Config cfg, string text)
    {
        if (text.Length == 0)
        {
            Console.WriteLine("Укажите текст: Jarvis.exe --say \"открой блокнот\"");
            return 1;
        }

        using var voice = new Voice(cfg);
        Console.WriteLine($"Голос: {voice.VoiceName}");
        voice.Say(text);
        voice.Start();
        voice.WaitUntilIdle(30000);
        return 0;
    }

    /// <summary>Разведка экранного пути: тот же код, что стоит за командой «перевод экрана»,
    /// только без озвучки. Показывает, стоит ли в Windows OCR-пакет нужного языка и отвечает ли переводчик.</summary>
    private static int ScreenMode(string[] args)
    {
        // `--screen yandex` — тот же снимок, но отдаётся Яндекс Переводчику: кадр в буфер
        // обмена и открытая страница, без распознавания и вписки.
        if (args.Length > 1 && args[1].Equals("yandex", StringComparison.OrdinalIgnoreCase))
        {
            var shot = ScreenTranslate.ToYandex();
            Console.WriteLine(shot.Error is null
                ? "кадр в буфере обмена, страница Яндекс Переводчика открыта"
                : $"экран не снят: {shot.Error}");
            return shot.Error is null ? 0 : 1;
        }

        Console.WriteLine($"OCR-пакеты: {ScreenTranslate.Engines()}");
        var watch = Stopwatch.StartNew();
        var result = ScreenTranslate.Run();
        Console.WriteLine($"{watch.ElapsedMilliseconds} мс · строк {result.Lines} · направление {result.Target} · ошибка {(result.Error ?? "нет")}");
        if (result.Text.Length > 0) Console.WriteLine(result.Text);

        if (result.Photo is { } photo)
        {
            using (photo)
            {
                var path = args.Length > 1 ? args[1] : string.Empty;
                if (path.Length == 0) return result.Error is null ? 0 : 1;
                photo.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine($"кадр с переводом: {path}");
            }
        }

        return result.Error is null ? 0 : 1;
    }

    /// <summary>Распознавание по картинке, без захвата экрана: проверка пакетов OCR и того,
    /// насколько хорошо Windows читает именно этот шрифт. Со вторым адресом добавляется
    /// перевод с впиской в файл — полный путь команды, только по статичному снимку.</summary>
    private static int OcrMode(string[] args)
    {
        Console.WriteLine($"OCR-пакеты: {ScreenTranslate.Engines()}");
        var path = args.Length > 1 ? args[1] : string.Empty;
        if (!File.Exists(path))
        {
            Console.WriteLine("Укажите файл: Jarvis.exe --ocr снимок.png [перевод.png]");
            return 1;
        }

        if (args.Length < 3)
        {
            var lines = ScreenTranslate.RecognizeFile(path);
            foreach (var line in lines) Console.WriteLine(line);
            Console.WriteLine($"строк: {lines.Length}");
            return lines.Length == 0 ? 1 : 0;
        }

        var result = ScreenTranslate.RunFile(path);
        Console.WriteLine($"строк {result.Lines} · направление {result.Target} · ошибка {(result.Error ?? "нет")}");
        if (result.Photo is { } photo)
        {
            using (photo)
            {
                photo.Save(args[2], System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine($"переведённый кадр: {args[2]}");
            }
        }

        return result.Error is null ? 0 : 1;
    }

    private static int Listen(Config cfg, string[] args)
    {
        var seconds = args.Length > 1 && int.TryParse(args[^1], out var parsed) ? parsed : 6;
        Console.WriteLine($"Слушаю {seconds} с...");
        using var model = new Native.Model(cfg.ModelPath);
        using var recognizer = OpenRecognizer(cfg, model);
        using var voice = new Voice(cfg);
        var mic = cfg.Microphone >= 0 ? cfg.Microphone : Microphones.PickWorking(cfg.SampleRate);
        if (mic < 0)
        {
            Console.WriteLine("Микрофон не найден.");
            return 2;
        }

        using var listener = new Listener(mic, cfg.SampleRate);
        listener.Attach(recognizer);
        listener.Start();
        var started = Environment.TickCount64;
        var deadline = started + seconds * 1000L;
        var heard = new StringBuilder();
        while (Environment.TickCount64 < deadline)
        {
            if (listener.TryTakeUtterance(out var utterance)) heard.Append(utterance).Append(' ');
            Thread.Sleep(50);
        }

        var window = listener.CaptureSince(started);
        heard.Append(listener.Flush());
        listener.Stop();
        Console.WriteLine($"Захват: {listener.CaptureRate} Гц, буферов получено: {listener.BufferCount}");
        var text = heard.ToString().Trim();
        Console.WriteLine(text.Length == 0 ? "(ничего не распознано)" : $"B: {text}");

        // Нейросеть слышит фразу целиком: по ней видно, был ли звук вообще и где ошиблась модель.
        if (cfg.NeuralAsr && Directory.Exists(cfg.NeuralAsrPath))
        {
            using var neural = GigaAm.Open(cfg.NeuralAsrPath, cfg.NeuralAsrThreads);
            var neuralText = Commands.Normalize(neural.Decode(window, cfg.SampleRate));
            Console.WriteLine($"GigaAM: {(neuralText.Length == 0 ? "(пусто)" : neuralText)}");
            if (text.Length == 0) text = neuralText;
        }

        if (text.Length == 0) return 3;

        var commands = new Commands(voice, cfg);
        voice.Start();
        Console.WriteLine($"Команда: {text}");
        Console.WriteLine($"Ответ: {commands.Execute(text)}");
        voice.WaitUntilIdle(15000);
        return 0;
    }

    private static int Transcribe(Config cfg, string wavPath)
    {
        if (wavPath.Length == 0 || !File.Exists(wavPath))
        {
            Console.WriteLine("Укажите WAV-файл: Jarvis.exe --transcribe audio.wav");
            return 1;
        }

        var samples = Pcm.ReadWav(wavPath, out var rate);
        if (rate != cfg.SampleRate) samples = Pcm.Resample(samples, rate, cfg.SampleRate);
        using var model = new Native.Model(cfg.ModelPath);
        using var recognizer = OpenRecognizer(cfg, model);

        var text = new StringBuilder();
        var raw = Environment.GetEnvironmentVariable("JARVIS_RAW") == "1";
        const int ChunkSamples = 1600;
        for (var offset = 0; offset < samples.Length; offset += ChunkSamples)
        {
            var taken = Math.Min(ChunkSamples, samples.Length - offset);
            var piece = samples[offset..(offset + taken)];
            var endpoint = recognizer.Accept(piece, taken);
            if (raw && offset % 16000 == 0) Console.WriteLine($"raw partial: {recognizer.PartialRaw()}");
            if (endpoint)
            {
                if (raw) Console.WriteLine($"raw result: {recognizer.ResultRaw()}");
                text.Append(recognizer.Result()).Append(' ');
            }
        }

        if (raw) Console.WriteLine($"raw final: {recognizer.FlushRaw()}");
        text.Append(recognizer.Flush());
        Console.WriteLine(text.ToString().Trim().Length == 0 ? "(пусто)" : text.ToString().Trim());

        if (cfg.NeuralAsr)
        {
            using var neural = GigaAm.Open(cfg.NeuralAsrPath, cfg.NeuralAsrThreads);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var neuralText = neural.Decode(samples, cfg.SampleRate);
            Console.WriteLine($"GigaAM ({stopwatch.ElapsedMilliseconds} мс): {(neuralText.Length == 0 ? "(пусто)" : neuralText)}");
        }

        return 0;
    }

    /// <summary>Пишет сигнал микрофона в WAV и сразу прогоняет его через Vosk: видно и уровень, и текст.</summary>
    private static int Record(Config cfg, string[] args)
    {
        var seconds = args.Length > 1 && int.TryParse(args[1], out var parsed) ? Math.Clamp(parsed, 1, 60) : 5;
        var mic = args.Length > 2 && int.TryParse(args[2], out var device) ? device : cfg.Microphone >= 0 ? cfg.Microphone : Microphones.PickDefault();
        if (mic < 0)
        {
            Console.WriteLine("Микрофон не найден.");
            return 2;
        }

        var path = args.Length > 3 && !args[3].StartsWith('-')
            ? Path.GetFullPath(args[3])
            : Path.Combine(Path.GetTempPath(), "jarvis-mic.wav");

        var captured = new List<short>();
        using var wave = new NAudio.Wave.WaveInEvent
        {
            DeviceNumber = mic,
            WaveFormat = new NAudio.Wave.WaveFormat(cfg.SampleRate, 16, 1),
            BufferMilliseconds = 100
        };
        wave.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0)
                lock (captured) captured.AddRange(MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, e.BytesRecorded)));
        };

        Console.WriteLine($"Пишу {seconds} с с микрофона #{mic}...");
        wave.StartRecording();
        Thread.Sleep(seconds * 1000);
        wave.StopRecording();
        wave.Dispose();

        short[] samples;
        lock (captured) samples = captured.ToArray();
        Wav.Write(path, samples, cfg.SampleRate);

        long sum = 0;
        var peak = 0;
        foreach (var sample in samples)
        {
            sum += (long)sample * sample;
            var level = Math.Abs((int)sample);
            if (level > peak) peak = level;
        }

        var rms = samples.Length == 0 ? 0 : Math.Sqrt(sum / (double)samples.Length);
        Console.WriteLine($"Файл: {path}");
        Console.WriteLine($"Отсчётов: {samples.Length}, пик: {peak} ({peak / 327.68:F1}% шкалы), RMS: {rms:F0}");
        if (peak < 300) Console.WriteLine("Сигнал почти нулевой: микрофон выключен, заглушён или выбран не тот.");
        return Transcribe(cfg, path);
    }

    /// <summary>Озвучить текст в WAV-файл: нужно, чтобы проверить распознавание без микрофона.</summary>
    private static int WavMode(Config cfg, string[] args)
    {
        var text = Value(args, "--wav");
        if (text.Length == 0)
        {
            Console.WriteLine("Укажите текст: Jarvis.exe --wav \"джарвис открой блокнот\" [путь.wav] [усиление]");
            return 1;
        }

        var rest = args.Skip(2).Where(a => !a.StartsWith('-')).ToList();
        var path = rest.Count > 0 ? Path.GetFullPath(rest[0]) : Path.Combine(Path.GetTempPath(), "jarvis-tts.wav");
        var gain = rest.Count > 1 && float.TryParse(rest[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 0.1f, 20f)
            : 1f;

        using var voice = new Voice(cfg);
        voice.SpeakToWave(text, path, gain);
        Console.WriteLine($"{path}, усиление {gain:F1}, голос «{voice.VoiceName}»");
        return 0;
    }

    /// <summary>Выбор микрофона в настройках: Jarvis.exe --mic 2 или --mic auto.</summary>
    private static int MicMode(Config cfg, string[] args)
    {
        Microphones.Print();
        Console.WriteLine($"Сейчас в jarvis.json: microphone = {Describe(cfg.Microphone)}");
        if (args.Length < 2)
        {
            Console.WriteLine("Выбрать устройство: Jarvis.exe --mic <номер>");
            Console.WriteLine("Вернуть автовыбор:  Jarvis.exe --mic auto");
            return 0;
        }

        var value = args[1];
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            cfg.Microphone = -1;
        }
        else if (int.TryParse(value, out var index) && index >= 0 && index < NAudio.Wave.WaveInEvent.DeviceCount)
        {
            cfg.Microphone = index;
        }
        else
        {
            Console.WriteLine($"Нет такого микрофона: {value}");
            return 1;
        }

        cfg.Save();
        Console.WriteLine($"Сохранено в {cfg.ConfigPath}: microphone = {Describe(cfg.Microphone)}");
        return 0;
    }

    private static string Describe(int microphone) => microphone < 0 ? "auto (ищем живой при старте)" : microphone.ToString();

    private static int Plan(Config cfg, string[] args)
    {
        Guard.DryRun = true;
        using var voice = new Voice(cfg);
        var commands = new Commands(voice, cfg);
        var phrases = args.Skip(1).Where(a => !a.StartsWith('-')).ToList();
        if (phrases.Count == 0)
            phrases =
            [
                "джарвис открой блокнот", "закрой хром", "закрой все окна", "найди погоду в санкт-петербурге",
                "следующий трек", "тише", "громкость 40", "звук отключи", "что открыто",
                "который час", "какое сегодня число", "курс доллара", "переведи привет на английский",
                "сколько будет двенадцать на три", "полный экран", "перемотай назад", "сценарий утро",
                "сверни окно", "спасибо", "запусти калькулятор", "режим ожидания", "выход",
                "абракадабра",
            ];

        foreach (var phrase in phrases)
        {
            var reply = commands.Execute(phrase);
            Console.WriteLine($"{phrase,-40} -> {reply}");
        }

        Console.WriteLine(Guard.DryRun ? "(сухой прогон: действия не выполнялись)" : "");
        return 0;
    }

    private static int Voices(Config cfg, string[] args)
    {
        var names = PiperTts.InstalledVoices(cfg.PiperVoicesPath).ToList();
        if (names.Count == 0)
        {
            Console.WriteLine($"Голосов нет в {cfg.PiperVoicesPath}");
            return 1;
        }

        var demo = args.Skip(1).Any(a => a.Equals("demo", StringComparison.OrdinalIgnoreCase));
        var text = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-') && !a.Equals("demo", StringComparison.OrdinalIgnoreCase))
                   ?? cfg.Greeting;
        Console.WriteLine($"Каталог голосов: {cfg.PiperVoicesPath}");
        foreach (var name in names)
        {
            var active = name.Contains(cfg.PiperVoice, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"{(active ? "*" : " ")} {name}{(active ? "  (выбран в jarvis.json)" : "")}");
            if (!demo) continue;
            try
            {
                var watch = Stopwatch.StartNew();
                using var tts = new PiperTts(Path.Combine(cfg.PiperVoicesPath, name), cfg.EspeakDataPath, cfg.PiperSpeed, cfg.PiperThreads);
                var pcm = tts.Render(text);
                var seconds = pcm.Length / (double)tts.SampleRate;
                var outPath = Path.Combine(cfg.DictationDir, $"голос-{name}.wav");
                Directory.CreateDirectory(cfg.DictationDir);
                Wav.Write(outPath, pcm, tts.SampleRate);
                Console.WriteLine($"    {seconds:F1} с звука, {watch.ElapsedMilliseconds} мс → {outPath}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"    ошибка: {e.Message}");
            }
        }

        if (!demo) Console.WriteLine("Прослушать каждый голос: Jarvis.exe --voices demo");
        return 0;
    }

    /// <summary>Умные ответы без микрофона и без звука: жив ли Ollama, есть ли модель,
    /// а с вопросом — что она ответит.</summary>
    private static int BrainMode(Config cfg, string[] args)
    {
        Console.WriteLine($"Ollama: {cfg.OllamaUrl} · модель: {cfg.OllamaModel} · " +
                          $"ожидание: {cfg.BrainTimeoutSeconds} с · функция: {(cfg.SmartAnswers ? "включена" : "выключена")}");
        Console.WriteLine(Brain.ReportAsync(cfg, args.Length > 1 ? string.Join(' ', args[1..]) : string.Empty)
            .GetAwaiter().GetResult());
        return 0;
    }

    private static int VolumeMode(Config cfg, string[] args)    {
        if (args.Length < 2)
        {
            Console.WriteLine($"Громкость: {Volume.Get() * 100:F0}% (mute: {Volume.IsMuted})");
            return 0;
        }

        if (args[1] == "mute") Volume.Mute(true);
        else if (args[1] == "unmute") Volume.Mute(false);
        else if (int.TryParse(args[1], out var percent)) Volume.Set(percent / 100f);
        else return 1;

        Console.WriteLine($"Готово: {Volume.Get() * 100:F0}% (mute: {Volume.IsMuted})");
        return 0;
    }

    /// <summary>Распознаватель как у помощника: словарь команд. JARVIS_FREE=1 включает
    /// свободную модель — полезно сравнить качество.</summary>
    private static Native.Recognizer OpenRecognizer(Config cfg, Native.Model model)
    {
        if (Environment.GetEnvironmentVariable("JARVIS_FREE") != "1")
        {
            var restricted = model.CreateGrammarRecognizer(cfg.SampleRate, Vocabulary.For(cfg.WakeWords));
            if (restricted is not null) return restricted;
            Console.WriteLine("[словарь] не поднялся, работаю свободной моделью");
        }

        return model.CreateRecognizer(cfg.SampleRate);
    }

    /// <summary>Снимок панели в PNG: видно вёрстку, но окно не появляется, трей не создаётся,
    /// микрофон и глобальные клавиши не трогаются.</summary>
    private static int RenderPanel(Config cfg, string[] args)
    {
        var path = args.Length > 1 && !args[1].StartsWith('-')
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "jarvis-panel.png");
        var view = args.Skip(1).Any(a => a.Equals("--scenarios", StringComparison.OrdinalIgnoreCase)) ? 2
            : args.Skip(1).Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;

        var ok = false;
        var thread = new Thread(() =>
        {
            try
            {
                using var panel = new Shell(cfg, null, false);
                panel.Capture(path, view);
                ok = true;
            }
            catch (Exception e)
            {
                Log.Line($"рендер: {e}");
                Console.WriteLine($"[рендер] {e.Message}");
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Console.WriteLine(ok ? path : "панель не отрисована");
        return ok ? 0 : 1;
    }

    [DllImport("kernel32.dll")] private static extern bool FreeConsole();

    private static void Help()
    {
        Console.WriteLine("""
            Jarvis — голосовой помощник для Windows (офлайн).

            Jarvis.exe                      панель: вкладки «Пульт», «Настройки» и «Сценарии»,
                                            помощник в трее; F6 — слух, Ctrl+Alt+Space — окно
            Jarvis.exe --console            то же самое, но в обычном консольном окне, без панели
            Jarvis.exe --say "текст"        произнести текст вслух
            Jarvis.exe --apps               какие программы помощник находит по имени
            Jarvis.exe --listen [сек]       одно распознавание с микрофона + выполнение
            Jarvis.exe --transcribe file.wav расшифровать WAV (16 бит PCM)
            Jarvis.exe --mics               список микрофонов и их уровни записи
            Jarvis.exe --mic [номер|auto]   выбрать микрофон в настройках
            Jarvis.exe --ui                 только панель настроек, без голосовой сессии
            Jarvis.exe --render [файл.png] [--settings|--scenarios] снимок панели в файл, без показа окна
            Jarvis.exe --record [сек] [номер] записать микрофон в WAV и распознать
            Jarvis.exe --wav "текст" [файл] [усиление] озвучить текст в WAV-файл
            Jarvis.exe --voices             список нейросетевых голосов
            Jarvis.exe --voices demo [текст] записать пример каждого голоса в папку диктовки
            Jarvis.exe --windows            список открытых окон
            Jarvis.exe --volume [число|mute|unmute] громкость вывода
            Jarvis.exe --weather [город]    ответ сервиса погоды, тем же кодом, что и голосовая команда
            Jarvis.exe --course [валюта]    курсы Центробанка
            Jarvis.exe --translate "текст"  перевод
            Jarvis.exe --screen [файл.png]  распознать и перевести текст с экрана
            Jarvis.exe --screen yandex      снять экран в буфер обмена и открыть Яндекс Переводчик
            Jarvis.exe --ocr файл.png [перевод.png] распознать картинку, вторым адресом — перевести и вписать
            Jarvis.exe --calc "пример"      калькулятор, числа можно словами
            Jarvis.exe --scenario           список сценариев из jarvis.json
            Jarvis.exe --sites              сайты, которые помощник открывает по имени
            Jarvis.exe --brain [вопрос]     проверка локальной нейросети (Ollama): адрес, модель, ответ
            Jarvis.exe --selftest           проверка модели, голоса и цепочки TTS→STT без микрофона
            Jarvis.exe --plan ["фраза" ...] сухой прогон маршрутизации команд (без действий и звука)

            Команды помощника: открой/запусти, открой сайт X, закрой, найди, следующий трек, пауза, стоп музыка,
            тише/громче, громкость 40, звук отключи, что открыто, сверни, разверни, полный экран,
            перемотай, диктуй, сохраняю, который час, какое сегодня число, погода [в городе],
            курс доллара, переведи X на английский, перевод экрана, посчитай, сценарий утро, повтори,
            режим ожидания, выход.
            """);
    }
}
