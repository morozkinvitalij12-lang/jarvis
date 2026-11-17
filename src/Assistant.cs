using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Jarvis;

internal sealed class Assistant(Config cfg)
{
    /// <summary>Реплика-подсказка на время ответа нейросети: без неё пауза в несколько секунд
    /// выглядит как зависший помощник.</summary>
    private const string ThinkingCue = "Секунду, уточняю.";

    private readonly CancellationTokenSource _cancel = new();
    private readonly ConcurrentQueue<string> _typed = new();
    private Commands? _commands;
    private Dictation? _dictation;
    private Voice? _voice;

    /// <summary>Для панели: фаза, пауза и последняя услышанная команда.</summary>
    public volatile string Phase = "запуск";
    public volatile bool Paused;
    public volatile string LastHeard = string.Empty;
    public bool Running { get; private set; }

    public void Quit()
    {
        if (_commands is { } commands) commands.Quit = true;
        _cancel.Cancel();
    }

    /// <summary>Команда, набранная в окне: попадает в тот же разбор, что и речь.</summary>
    public void Submit(string text)
    {
        if (text.Trim().Length > 0) _typed.Enqueue(text.Trim());
    }

    private async Task PumpAsync()
    {
        while (_typed.TryDequeue(out var text))
        {
            if (_commands is not { } commands || _dictation is not { } dictation || _voice is not { } voice) return;
            Console.WriteLine($"П: {text}");
            LastHeard = text;
            await HandleAsync(text, text, dictation, commands, voice);
        }
    }

    /// <summary>Сессия живёт в отдельном потоке без контекста синхронизации (он есть у панели),
    /// поэтому ожидание здесь никого не блокирует.</summary>
    public int Run() => RunAsync().GetAwaiter().GetResult();

    private async Task<int> RunAsync()
    {
        Phase = "запуск";
        Console.WriteLine("JARVIS: модель распознавания...");
        var watch = Stopwatch.StartNew();
        using var model = new Native.Model(cfg.ModelPath);
        Phase = "прогрев";
        Console.WriteLine($"JARVIS: модель готова, {watch.ElapsedMilliseconds} мс.");

        using var voice = new Voice(cfg);
        var commands = new Commands(voice, cfg);
        var dictation = new Dictation(cfg);
        _voice = voice;
        _commands = commands;
        _dictation = dictation;
        Console.WriteLine($"JARVIS: голос «{voice.VoiceName}». Предпрогрев реплик...");

        if (cfg.Microphone < 0) Console.WriteLine("JARVIS: ищу живой микрофон...");
        var micIndex = cfg.Microphone >= 0 ? cfg.Microphone : Microphones.PickWorking(cfg.SampleRate);
        if (micIndex < 0)
        {
            Console.WriteLine("Микрофон не найден. Запустите: Jarvis.exe --mics");
            return 2;
        }

        // Распознаватели создаются один раз на сессию: поток микрофона держит в них нативные
        // указатели, поэтому пересоздавать их между фразами нельзя.
        using var free = model.CreateRecognizer(cfg.SampleRate);
        var vocabulary = Vocabulary.For(cfg.WakeWords);
        using var restricted = model.CreateGrammarRecognizer(cfg.SampleRate, vocabulary);
        var primary = restricted ?? free;
        var secondary = ReferenceEquals(primary, free) ? null : free;
        Console.WriteLine(restricted is null
            ? "JARVIS: словарь команд не поднялся, слушаю свободную модель."
            : $"JARVIS: словарь команд ({vocabulary.Count - 1} слов) + свободная модель для аргументов.");

        using var listener = new Listener(micIndex, cfg.SampleRate);
        Console.WriteLine($"JARVIS: микрофон #{micIndex}, захват {listener.CaptureRate} Гц.");
        listener.Attach(primary, secondary);

        // Нейросетевой распознаватель поднимается с секундой и гигабайтом памяти, поэтому грузится
        // в фоне: пока он не готов, фразы разбирает словарьный Vosk.
        var neuralLoad = cfg.NeuralAsr ? Task.Run(() => GigaAm.Open(cfg.NeuralAsrPath, cfg.NeuralAsrThreads)) : null;

        voice.Prewarm(Phrases.Prewarm);
        voice.Start();
        listener.Start();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            commands.Quit = true;
        };

        Console.WriteLine($"JARVIS: {cfg.Greeting} Скажите «{cfg.WakeWords[0]}»."
                          + (cfg.SmartAnswers ? $" Умные ответы: {cfg.OllamaModel} на {cfg.OllamaUrl}." : string.Empty));
        voice.Say(cfg.Greeting);

        Running = true;
        var listening = true;
        try
        {
            Phase = "ожидание";
            while (!commands.Quit)
            {
                await PumpAsync();

                if (Paused)
                {
                    if (listening)
                    {
                        listening = false;
                        listener.Stop();
                        Phase = "пауза";
                        Console.WriteLine("JARVIS: пауза — не слушаю и молчу.");
                    }

                    await Pause(150);
                    continue;
                }

                if (!listening)
                {
                    try
                    {
                        listener.BeginPhase();
                        listener.Start();
                        Phase = "ожидание";
                        listening = true;
                        Console.WriteLine("JARVIS: снова слушаю.");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"JARVIS: микрофон не вернулся — {e.Message}");
                        Paused = true;
                    }

                    continue;
                }

                var (heard, direct, wakeTick) = await WaitForWakeAsync(listener, commands);
                if (!heard) break;

                Phase = "команда";
                var command = direct ?? await CaptureCommandAsync(listener, commands);
                var argument = listener.FreeText;
                (command, argument) = MergeNeural(command, argument, listener, neuralLoad, wakeTick);
                if (command is null && argument.Length == 0)
                {
                    Console.WriteLine("JARVIS: не слышу команд.");
                    voice.Say("Не слышу команд.");
                    Phase = "ожидание";
                    continue;
                }

                command ??= argument;
                Console.WriteLine($"B: {command}");
                LastHeard = command;
                await HandleAsync(command, argument, dictation, commands, voice);
                Phase = "ожидание";
            }
        }
        finally
        {
            Running = false;
            Phase = "стоп";
            listener.Stop();
            voice.WaitUntilIdle(4000);
            if (neuralLoad is { IsCompletedSuccessfully: true }) neuralLoad.Result.Dispose();
        }

        return 0;
    }

    private async Task HandleAsync(string transcript, string argument, Dictation dictation, Commands commands, Voice voice)
    {
        var text = Commands.Normalize(transcript);
        var freeText = Commands.Normalize(argument);

        if (dictation.Active)
        {
            // Конец диктовки принимаем только короткой фразой: иначе любое «стоп» внутри
            // надиктованного текста обрывает запись.
            var said = text.Length > 0 ? text : freeText;
            var brief = said.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2;

            if (brief && (said.Contains("сохраняю") || said.Contains("сохрани") || said.Contains("стоп")))
            {
                var path = dictation.Save();
                var reply = path.Length == 0 ? "Нечего сохранять." : $"Сохраняю в {Path.GetFileName(path)}.";
                Console.WriteLine($"JARVIS: {reply} Файл: {path}");
                voice.Say(reply);
            }
            else if (brief && (said.Contains("отменяю") || said.Contains("отмени")))
            {
                dictation.Cancel();
                Console.WriteLine("JARVIS: отменяю запись.");
                voice.Say("Отменяю запись.");
            }
            else
            {
                dictation.Append(argument.Length > 0 ? argument : transcript);
                var reply = $"Записал {dictation.Words} слов.";
                Console.WriteLine($"JARVIS: {reply}");
                voice.Say(reply);
            }

            return;
        }

        // «Диктуй» маленькая модель не знает, поэтому команду слышит только свободная модель.
        if (text.Contains("диктовку") || text.Contains("печатать") || text.Contains("запиши текст")
            || freeText.Contains("диктуй") || freeText.Contains("диктовку"))
        {
            dictation.Begin();
            var reply = "Диктую. Скажите «сохраняю» или «отменяю».";
            Console.WriteLine($"JARVIS: {reply}");
            voice.Say(reply);
            return;
        }

        // Словарь команд точен, но режет свободные аргументы: «найди погоду в Москве» он
        // слышит как «найди». Поэтому сначала пробуем словарь молча, а при неудаче — полную
        // расшифровку, и озвучиваем уже окончательный ответ.
        commands.SpeakReplies = false;
        var answer = commands.Execute(transcript);
        if (!commands.Understood && freeText.Length > 0 && freeText != text) answer = commands.Execute(argument);
        commands.SpeakReplies = true;

        // Фраза не похожа ни на одну команду — спрашиваем локальную нейросеть. Если её нет или
        // она промолчала, остаётся обычный ответ словаря: помощник не падает и не повторяется.
        if (!commands.Understood && await AskBrain(argument.Length > 0 ? argument : transcript, commands, voice)) return;

        if (answer.Length == 0) return;
        Console.WriteLine($"JARVIS: {answer}");
        voice.Say(answer);
    }

    /// <summary>True — нейросеть ответила, и этот ответ уже стоит в очереди озвучки.</summary>
    private async Task<bool> AskBrain(string heard, Commands commands, Voice voice)
    {
        if (!cfg.SmartAnswers) return false;
        var question = TextMatch.StripWake(heard, cfg.WakeWords);
        if (question.Trim().Length == 0) return false;

        var (status, reply) = await Brain.AskAsync(cfg, question, _cancel.Token, () =>
        {
            Console.WriteLine($"[мозг] вопрос: {question}");
            voice.Say(ThinkingCue);
        });
        if (reply.Length == 0) return false;

        commands.Remember(reply);
        Console.WriteLine($"[мозг] {status}: {reply}");
        voice.Say(reply);
        return true;
    }

    private async Task<(bool Heard, string? Direct, long WakeTick)> WaitForWakeAsync(Listener listener, Commands commands)
    {
        listener.BeginPhase();

        while (!commands.Quit)
        {
            if (listener.TryTakeUtterance(out var utterance) && TextMatch.ContainsWake(utterance, cfg.WakeWords))
            {
                // Одно только «Джарвис» командой не считается: иначе помощник ответит на вздох
                // и пропустит то, что человек скажет следом.
                var alone = TextMatch.StripWake(utterance, cfg.WakeWords);
                // Команда прозвучала вместе с именем — нейросети нужен отрезок задним числом.
                return (true, alone.Length > 0 ? alone : null,
                    Environment.TickCount64 - (alone.Length > 0 ? WakeLookbackMs : 0));
            }

            // Имя могло прозвучать в середине фразы. Отвечать сразу нельзя: команда доедет
            // половинкой, и помощник перебивает человека ответом «не понял».
            if (TextMatch.ContainsWake(Draft(listener), cfg.WakeWords))
                return (true, TextMatch.StripWake(await SettleAsync(listener), cfg.WakeWords),
                    Environment.TickCount64 - WakeLookbackMs);

            await PumpAsync();
            await Pause(40);
        }

        return (false, null, 0);
    }

    /// <summary>Словарный Vosk видит окончание фразы с задержкой, поэтому нейросетевой отрезок
    /// берут с запасом назад: четыре секунды хватает на «джарвис, открой браузер» целиком.
    /// Лишняя тишина в окне только вредит — модель достраивает по ней свои слова.</summary>
    private const long WakeLookbackMs = 4_000;

    /// <summary>Пауза, после которой фраза считается законченной.</summary>
    private long QuietMs(string heard)
        => Settled(heard) ? Math.Min(cfg.PartialTimeoutSeconds * 1000L, FastQuietMs) : cfg.PartialTimeoutSeconds * 1000L;

    /// <summary>Команда собрана: отвечать можно сразу, полная пауза здесь ощущается как зависший помощник.</summary>
    private static bool Settled(string heard)
    {
        var text = Commands.Normalize(heard);
        if (Vocabulary.Score(text) >= 2) return true;
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var only] && Vocabulary.Known(only);
    }

    private const long FastQuietMs = 1_100;

    /// <summary>Нейросетевая расшифровка всего окна звука точнее обеих словарных строк, но берём
    /// её только когда командных слов в ней не меньше: модель слышит связно и путает междометия.</summary>
    private (string? Command, string Argument) MergeNeural(string? command, string argument,
        Listener listener, Task<GigaAm>? load, long sinceTick)
    {
        if (load is not { IsCompletedSuccessfully: true }) return (command, argument);
        var neural = load.Result;
        if (!neural.IsReady) return (command, argument);

        var text = Commands.Normalize(neural.Decode(Pcm.TrimSilence(listener.CaptureSince(sinceTick), cfg.SampleRate), cfg.SampleRate));
        if (text.Length == 0) return (command, argument);

        var score = Vocabulary.Score(text);
        var fromGrammar = command is null ? -1 : Vocabulary.Score(Commands.Normalize(command));
        return (score >= fromGrammar ? text : command,
            score >= Vocabulary.Score(Commands.Normalize(argument)) ? text : argument);
    }

    /// <summary>Самый длинный черновик из двух потоков — по нему видно, говорит ли человек.</summary>
    private static string Draft(Listener listener)
    {
        var grammar = listener.Partial;
        var free = listener.FreeText.Length > 0 ? listener.FreeText : listener.FreePartial;
        return free.Length >= grammar.Length ? free : grammar;
    }

    /// <summary>Ждёт конца фразы: границу utterance или тишину после последних слов.
    /// Первая граница — ещё не конец: человек делает вдох посреди фразы, и ответ на половину
    /// команды звучит как перебивание.</summary>
    private async Task<string> SettleAsync(Listener listener)
    {
        var deadline = Environment.TickCount64 + cfg.CommandTimeoutSeconds * 1000L;
        var heard = new StringBuilder();
        var last = Draft(listener);
        var quietSince = Environment.TickCount64;

        while (Environment.TickCount64 < deadline)
        {
            if (listener.TryTakeUtterance(out var utterance) && utterance.Length > 0)
            {
                heard.Append(utterance).Append(' ');
                last = Draft(listener);
                quietSince = Environment.TickCount64;
                continue;
            }

            var draft = Draft(listener);
            if (draft != last)
            {
                last = draft;
                quietSince = Environment.TickCount64;
            }
            else if ((heard.Length > 0 || draft.Length > 0)
                     && Environment.TickCount64 - quietSince >= QuietMs($"{heard} {draft}"))
            {
                break;
            }

            await Pause(40);
        }

        var settled = heard.ToString().Trim();
        if (settled.Length > 0) return settled;

        var flushed = listener.Flush();
        return flushed.Length > 0 ? flushed : Draft(listener);
    }

    private async Task<string?> CaptureCommandAsync(Listener listener, Commands commands)
    {
        listener.BeginPhase();

        var started = Environment.TickCount64;
        var limit = started + (cfg.CommandTimeoutSeconds + cfg.PartialTimeoutSeconds) * 1000L;
        var deadline = started + cfg.CommandTimeoutSeconds * 1000L;
        var heard = new StringBuilder();
        var last = Draft(listener);
        var quietSince = started;

        while (!commands.Quit)
        {
            await PumpAsync();

            var now = Environment.TickCount64;
            if (now >= deadline) break;

            var quietMs = QuietMs($"{heard} {Draft(listener)}");

            if (listener.TryTakeUtterance(out var utterance) && utterance.Length > 0)
            {
                heard.Append(utterance).Append(' ');
                last = Draft(listener);
                quietSince = now;
            }
            else
            {
                var draft = Draft(listener);
                if (draft != last)
                {
                    last = draft;
                    quietSince = now;
                }
                else if ((heard.Length > 0 || draft.Length > 0) && now - quietSince >= quietMs)
                {
                    break;
                }
            }

            // Пока человек говорит, окно ожидания сдвигается: обрываем только по тишине.
            if (listener.SpeechInLast((int)quietMs)) deadline = Math.Min(limit, now + quietMs);

            await Pause(40);
        }

        var captured = heard.ToString().Trim();
        if (captured.Length > 0) return captured;

        var tail = listener.Flush();
        if (tail.Length > 0) return tail;
        var rest = Draft(listener);
        return rest.Length > 0 ? rest : null;
    }

    /// <summary>Шаг опроса микрофона. Выход снимается по «выход» или по F6, не дожидаясь
    /// следующего витка цикла.</summary>
    private async Task Pause(int milliseconds)
    {
        try
        {
            await Task.Delay(milliseconds, _cancel.Token);
        }
        catch (TaskCanceledException)
        {
        }
    }
}
