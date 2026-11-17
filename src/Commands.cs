using System.Text.RegularExpressions;

namespace Jarvis;

internal sealed class Commands(Voice voice, Config cfg)
{
    private static readonly Dictionary<string, string[]> Apps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["браузер"] = ["msedge", "chrome", "firefox"],
        ["интернет"] = ["msedge", "chrome", "firefox"],
        ["edge"] = ["msedge"],
        ["хром"] = ["chrome", "msedge"],
        ["фаерфокс"] = ["firefox"],
        ["проводник"] = ["explorer"],
        ["папки"] = ["explorer"],
        ["папка"] = ["explorer"],
        ["компьютер"] = ["explorer shell:MyComputerFolder"],
        ["этот компьютер"] = ["explorer shell:MyComputerFolder"],
        ["корзина"] = ["explorer shell:RecycleBinFolder"],
        ["блокнот"] = ["notepad"],
        ["калькулятор"] = ["calc"],
        ["счёт"] = ["calc"],
        ["настройки"] = ["ms-settings:"],
        ["параметры"] = ["ms-settings:"],
        ["терминал"] = ["wt", "cmd"],
        ["консоль"] = ["cmd"],
        ["командная строка"] = ["cmd"],
        ["код"] = ["code"],
        ["visual studio code"] = ["code"],
        ["ворд"] = ["winword"],
        ["word"] = ["winword"],
        ["эксель"] = ["excel"],
        ["excel"] = ["excel"],
        ["пауэрпойнт"] = ["powerpnt"],
        ["фото"] = ["microsoft.windowsphotos:", "ms-photos:"],
        ["галерея"] = ["ms-photos:"],
        ["магазин"] = ["ms-windows-store:"],
        ["диспетчер"] = ["taskmgr"],
        ["диспетчер задач"] = ["taskmgr"],
        ["пейнт"] = ["mspaint"],
        ["spotify"] = ["spotify"],
        ["спотифай"] = ["spotify"],
        ["музыка"] = ["spotify"],
        ["стим"] = ["steam"],
        ["тлаунчер"] = ["tlauncher"],
        ["тулба"] = ["tlauncher"],
        ["майнкрафт"] = ["minecraft"],
        ["сабниткрафт"] = ["subnautica"],
        ["дискорд"] = ["discord"],
        ["телеграм"] = ["telegram"],
        ["радин"] = ["radmin"],
        ["вандрайв"] = ["onedrive"],
        ["юбисофт"] = ["ubisoft"],
        ["нвидия"] = ["nvidia"],
        ["obs"] = ["obs64", "obs"],
        ["обс"] = ["obs64", "obs"],
    };

    private static readonly Dictionary<string, string[]> TitleSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["хром"] = ["chrome", "google"],
        ["браузер"] = ["edge", "chrome", "firefox"],
        ["вкладка"] = ["edge", "chrome"],
        ["проводник"] = ["проводник", "explorer"],
        ["папка"] = ["проводник", "explorer"],
        ["диск"] = ["проводник", "explorer"],
        ["код"] = ["code", "visual studio", "qoder"],
        ["редактор"] = ["code", "qoder", "блокнот", "notepad"],
        ["музыка"] = ["spotify", "winamp", "итунс", "media"],
        ["терминал"] = ["терминал", "cmd", "консоль", "terminal"],
        ["игра"] = ["terraria", "steam", "minecraft"],
        ["игровой"] = ["terraria", "steam", "minecraft"],
        ["стим"] = ["steam"],
        ["дискорд"] = ["discord"],
        ["телеграм"] = ["telegram"],
        ["спотифай"] = ["spotify"],
        ["обс"] = ["obs"],
    };

    private static string[] TargetsFor(string needle)
    {
        var key = CleanTarget(needle);
        var list = new List<string>();
        if (key.Length > 0) list.Add(key);
        foreach (var (alias, titles) in TitleSynonyms)
            if (key.Length > 0 && key.Contains(alias, StringComparison.Ordinal)) list.AddRange(titles);
        // «закрой стим»: окно подписано латиницей, имя берём из установленного софта.
        if (key.Length > 0 && AppsIndex.Resolve(key) is { } hit) list.Add(hit.Name);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static readonly string[] Fillers = ["окно", "окна", "все", "всё", "мои", "мне", "пожалуйста", "джарвис", "сэр"];

    private static string CleanTarget(string needle)
    {
        var kept = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => Array.IndexOf(Fillers, t) < 0);
        return string.Join(' ', kept).Trim();
    }

    private static readonly string[] Greetings = ["привет", "здравствуй", "добрый день", "доброе утро", "добрый вечер"];
    private static readonly string[] Thanks = ["спасибо", "благодарю"];

    private volatile bool _quit;
    private volatile bool _standby;

    public string LastReply { get; private set; } = string.Empty;

    /// <summary>Последняя фраза была понята как команда.</summary>
    public bool Understood { get; private set; }

    /// <summary>Когда есть запасная расшифровка, ответы не озвучиваются сразу: иначе помощник
    /// скажет «не понял» и следом правильный ответ.</summary>
    public bool SpeakReplies { get; set; } = true;

    /// <summary>Флаги ставит и панель управления из своего потока, поэтому читаем их как volatile.</summary>
    public bool Quit { get => _quit; set => _quit = value; }
    public bool StandbyRequested { get => _standby; set => _standby = value; }

    public static string Normalize(string text)
    {
        var lower = text.ToLowerInvariant().Replace('ё', 'е');
        return Regex.Replace(lower, "[^а-яa-z0-9%.:\\-\\s/]", " ").Replace("  ", " ").Trim();
    }

    private bool Has(string text, params string[] needles) => needles.Any(n => text.Contains(n, StringComparison.Ordinal));

    private static string After(string text, params string[] verbs)
    {
        foreach (var verb in verbs)
        {
            var index = text.IndexOf(verb + " ", StringComparison.Ordinal);
            if (index >= 0) return text[(index + verb.Length + 1)..].Trim();
        }

        return string.Empty;
    }

    public string Execute(string rawTranscript)
    {
        var text = Normalize(rawTranscript);
        Understood = false;
        if (text.Length == 0) return Miss("Не понял команду.");

        if (Has(text, "выход", "заверши работу", "отключись", "пока джарвис")) { Quit = true; return Reply("Отключаюсь. До связи."); }
        if (Has(text, "режим ожидания", "спи", "засыпай", "ожидай")) { StandbyRequested = true; return Reply("Перехожу в режим ожидания. Назовите моё имя, когда понадобится."); }
        if (Has(text, "повтори")) return Reply(LastReply);
        if (Has(text, "замолчи", "прекрати говорить", "перестань говорить")) { voice.StopSpeaking(); voice.Muted = true; return string.Empty; }
        if (Has(text, "говорите", "включи голос", "озвучку")) { voice.Muted = false; return Reply("Голос включён."); }

        if (Has(text, "который час", "сколько времени", "какое время"))
            return Reply($"Сейчас {Clock(DateTime.Now)}.");

        if (Has(text, "какое сегодня", "какой сегодня", "какое число", "какой день", "сегодня число"))
            return Reply($"Сегодня {DateTime.Now.Day} {Month(DateTime.Now.Month)}, {DayOfWeekName(DateTime.Now.DayOfWeek)} {DateTime.Now:yyyy} года.");

        if (Has(text, "сценарий", "сценарии")) return Scenario(text);

        // Сетевые вопросы идут до «найди» и до приветствий: иначе «переведи привет»
        // превратится в ответ на приветствие, а «погода в Москве» — в поисковый запрос.
        // Глагол окна важнее совпадения со словом сервиса: «закрой курсор» — это не про валюты.
        var windowCommand = Has(text, "открой", "открыть", "запусти", "закрой", "закрыть", "сверни", "разверни", "восстанови");

        if (!windowCommand && Has(text, "погода", "погод", "прогноз", "температур", "градус", "дождь", "снег", "метель"))
        {
            var reply = Weather.Report(City(text), cfg.WeatherCity);
            return reply is null ? Reply("Не смог связаться с сервисом погоды.") : Reply(reply);
        }

        if (!windowCommand && Has(text, "курс", "валют", "доллар", "евро", "юан", "фунт", "лир", "тенге", "гривн", "руп", "злот"))
        {
            var reply = Money.Report(text);
            return reply is null ? Reply("Центробанк не ответил.") : Reply(reply);
        }

        if (!windowCommand && Has(text, "экран") && Has(text, "перевод", "переведи", "перевести"))
            return Screen();

        if (!windowCommand && Has(text, "переведи", "переведите", "перевести", "перевод", "как будет"))
        {
            var reply = Translate.To(text);
            return reply is null ? Reply("Перевод недоступен или текст слишком длинный.") : Reply(reply);
        }

        if (!windowCommand && Has(text, "посчитай", "вычисли", "сколько будет", "равно", "плюс", "минус", "умножить", "разделить", "процент", "корень", "квадрат"))
        {
            var result = Calc.Evaluate(text);
            return result is null ? Miss("Не понял пример.") : Reply($"Получается {result}.");
        }

        if (MatchesAny(text, Greetings)) return Reply("Приветствую. Чем могу быть полезен?");
        if (MatchesAny(text, Thanks)) return Reply("Всегда к вашим услугам.");
        if (Has(text, "как дела", "как ты")) return Reply("Все системы в норме.");

        if (Has(text, "что открыто", "список окон", "какие окна"))
        {
            var windows = WinCtl.TopLevelWindows();
            var titles = windows.Take(5).Select(w => w.Title).ToList();
            var more = windows.Count > 5 ? $" и ещё {windows.Count - 5}" : string.Empty;
            return Reply(titles.Count == 0
                ? "Открытых окон нет."
                : $"Открыто {windows.Count} окон{more}: {string.Join(", ", titles)}.");
        }

        if (Has(text, "следующий трек", "след трек", "дальше музыку", "следующая песня", "следующую песню"))
        {
            Media.Command(Media.Next);
            return Reply("Следующий трек.");
        }

        if (Has(text, "предыдущий трек", "пред трек", "назад музыку", "предыдущая песня", "предыдущую песню"))
        {
            Media.Command(Media.Previous);
            return Reply("Предыдущий трек.");
        }

        if (Has(text, "стоп музыка", "музыка стоп", "останови музыку", "останови плеер"))
        {
            Media.Command(Media.Stop);
            return Reply("Музыка остановлена.");
        }

        if (Has(text, "пауза", "поставь на паузу", "приостанови"))
        {
            Media.Command(Media.PlayPause);
            return Reply("Пауза.");
        }

        if (Has(text, "продолжи музыку", "снова музыку", "играть музыку", "запусти музыку", "включи музыку"))
        {
            Media.Command(Media.PlayPause);
            return Reply("Продолжаю.");
        }

        if (Has(text, "полный экран", "на весь экран", "во весь экран", "разверни видео", "разверни экран"))
        {
            Media.Fullscreen(true);
            return Reply("Разворачиваю на весь экран.");
        }

        if (Has(text, "обычный экран", "выйти из полного", "small экран", "маленький экран"))
        {
            Media.Fullscreen(false);
            return Reply("Обычный режим.");
        }

        if (Has(text, "перемотай", "перемотка", "промотай", "вперед на", "назад на"))
        {
            var back = Has(text, "назад");
            Media.Seek(!back);
            return Reply(back ? "Мотаю назад." : "Мотаю вперёд.");
        }

        if (Has(text, "звук отключ", "без звука", "мьют") && !Has(text, "громче"))
        {
            Volume.Mute(true);
            return Reply("Звук отключён.");
        }

        if (Has(text, "звук включ", "верни звук", "снял мьют"))
        {
            Volume.Mute(false);
            return Reply("Звук включён.");
        }

        var percent = MatchPercent(text);
        if (percent >= 0 && Has(text, "громкость", "звук"))
        {
            Volume.Set(percent / 100f);
            return Reply($"Громкость {percent} процентов.");
        }

        if (Has(text, "громче", "прибавь звук", "погромче"))
        {
            Volume.Step(0.1f);
            return Reply("Громкость увеличена.");
        }

        if (Has(text, "тише", "убавь звук", "понизе"))
        {
            Volume.Step(-0.1f);
            return Reply("Громкость уменьшена.");
        }

        if (Has(text, "закрой все окна", "закрой всё", "закрыть все окна", "закрыть всё", "закрой все"))
        {
            var closed = WinCtl.CloseAll();
            return Reply(closed > 0 ? $"Закрываю {closed} окон." : "Нечего закрывать.");
        }

        if (Has(text, "закрой", "закрою", "закрыть", "close "))
        {
            var target = After(text, "закрой", "закрою", "закрыть", "close");
            var cleaned = CleanTarget(target);
            if (cleaned.Length == 0) return Miss("Что закрыть?");
            var closed = WinCtl.CloseWindows(TargetsFor(target));
            return Reply(closed > 0 ? $"Закрываю «{cleaned}»." : $"Окно «{cleaned}» не найдено.");
        }

        if (Has(text, "сверни", "минимизируй"))
        {
            var target = After(text, "сверни", "минимизируй");
            var count = WinCtl.ToggleWindows(TargetsFor(target), true);
            return Reply(count > 0 ? "Сворачиваю." : "Нечего сворачивать.");
        }

        if (Has(text, "разверни", "верни окно", "восстанови окно"))
        {
            var target = After(text, "разверни", "верни окно", "восстанови окно");
            var count = WinCtl.ToggleWindows(TargetsFor(target), false);
            return Reply(count > 0 ? "Разворачиваю." : "Нечего разворачивать.");
        }

        if (Has(text, "найди", "погугли", "поищи", "ищи", "гугл"))
        {
            var query = After(text, "найди", "погугли", "поищи", "ищи", "гугл", "google");
            if (query.Length == 0) return Miss("Что искать?");
            try
            {
                WinCtl.Search(query);
                return Reply($"Ищу в интернете: {query}.");
            }
            catch (Exception e)
            {
                return Reply($"Не удалось открыть поиск. {e.Message}");
            }
        }

        if (Has(text, "открой", "открыть", "запусти", "включи", "start "))
        {
            var target = After(text, "открой", "открыть", "запусти", "включи", "start");
            if (target.Length == 0) return Miss("Что открыть?");
            return Open(target);
        }

        return Miss("Не понял команду.");
    }

    /// <summary>Шаги идут через обычный разбор, поэтому сценарий не умеет ничего такого,
    /// чего нельзя сказать вслух. Ответы собираются в одну фразу, чтобы помощник
    /// не перебивал сам себя четырьмя объявлениями подряд.</summary>
    private string Scenario(string text)
    {
        var scenario = Scenarios.Find(cfg.Scenarios, text);
        if (scenario is null)
            return Reply(cfg.Scenarios.Length == 0
                ? "Сценариев нет. Допишите их в jarvis.json."
                : $"Такого сценария нет. Есть: {string.Join(", ", cfg.Scenarios.Select(s => s.Name))}.");

        var spokeBefore = SpeakReplies;
        SpeakReplies = false;
        var lines = new List<string>();
        try
        {
            foreach (var step in scenario.Steps)
            {
                if (Quit || StandbyRequested) break;
                Console.WriteLine($"ШАГ: {step}");
                if (Execute(step) is { Length: > 0 } answer) lines.Add(answer);
                Thread.Sleep(250);
            }
        }
        finally
        {
            SpeakReplies = spokeBefore;
        }

        return Reply($"Сценарий «{scenario.Name}». {string.Join(" ", lines)}");
    }

    /// <summary>Текст экрана читает встроенный в Windows OCR — он офлайн; перевод строк приходит
    /// по сети, и без неё помощник честно об этом говорит. Кадр показывает панель, а в консольном
    /// режиме перевода некуда положить, поэтому его остаётся только зачитать. Второй способ —
    /// Яндекс Переводчик: ему отдаётся сам снимок, и распознаёт уже он.</summary>
    private string Screen()
    {
        if (cfg.YandexScreen)
        {
            if (Guard.DryRun) return Reply("Снимаю экран и отдаю его Яндекс Переводчику.");

            voice.Say("Секунду, снимаю экран.");
            var shot = ScreenTranslate.ToYandex();
            return shot.Error is null
                ? Reply("Снимок экрана в буфере обмена. В Яндекс Переводчике откройте вкладку «Картинка» и вставьте его по Ctrl+V.")
                : Reply($"Экран не снят: {shot.Error}.");
        }

        if (Guard.DryRun) return Reply("Читаю экран и перевожу его.");

        voice.Say("Секунду, читаю экран.");
        var result = ScreenTranslate.Run();
        if (result.Photo is null || result.Lines == 0) return Reply($"Экран не переведён: {result.Error}.");

        if (!ScreenOverlay.Show(result.Photo)) return Reply(result.Text);
        return Reply($"Перевод {(result.Target == "ru" ? "на русский" : "на английский")} — на экране.");
    }

    /// <summary>«погода в санкт-петербурге» → «санкт-петербург»: город берётся из падежной
    /// формы, поэтому с неё снимаем хвосты, которые wttr.in не разбирает.</summary>
    private static string City(string text)
    {
        var place = CleanTarget(After(text, "в", "для", "надо"));
        if (place.Length == 0) return string.Empty;
        return Regex.IsMatch(place, "^[а-я- ]+$") ? place : string.Empty;
    }

    /// <summary>Время прописью: синтезатор читает «1:50» как «один дробь пятьдесят»,
    /// а «1 час 50 минут» — нормально.</summary>
    private static string Clock(DateTime now)
    {
        var noon = now.Hour switch { 0 => "полночь", 12 => "полдень", _ => string.Empty };
        var hours = $"{now.Hour} {Weather.Plural(now.Hour, "час", "часа", "часов")}";
        if (now.Minute == 0) return noon.Length > 0 ? noon : $"{hours} ровно";

        var minutes = $"{now.Minute} {Weather.Plural(now.Minute, "минута", "минуты", "минут")}";
        return noon.Length > 0 ? $"{noon} {minutes}" : $"{hours} {minutes}";
    }

    private static string Month(int number) => number switch
    {
        1 => "января", 2 => "февраля", 3 => "марта", 4 => "апреля", 5 => "мая", 6 => "июня",
        7 => "июля", 8 => "августа", 9 => "сентября", 10 => "октября", 11 => "ноября", _ => "декабря",
    };

    private static string DayOfWeekName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "понедельник", DayOfWeek.Tuesday => "вторник", DayOfWeek.Wednesday => "среда",
        DayOfWeek.Thursday => "четверг", DayOfWeek.Friday => "пятница", DayOfWeek.Saturday => "суббота",
        _ => "воскресенье",
    };

    private string Open(string target)
    {
        var clean = target.Replace(" сайт", "").Replace(" страницу", "").Trim();
        if (LooksLikeAddress(clean))
        {
            try
            {
                WinCtl.OpenUrl(clean.Contains("://") ? clean : "https://" + clean);
                return Reply($"Открываю {clean}.");
            }
            catch (Exception e)
            {
                return Reply($"Не получилось открыть сайт. {e.Message}");
            }
        }

        foreach (var alias in Apps.Keys.OrderByDescending(k => k.Length))
            if (clean.Contains(alias, StringComparison.Ordinal) && TryOpen(Apps[alias], out var named))
                return Reply($"Открываю {named}.");

        if (TryOpen([clean], out var found)) return Reply($"Открываю {found}.");
        return Miss($"Не нашёл «{clean}».");
    }

    /// <summary>Кандидаты пробуются дважды: как команда Windows и как имя из установленного
    /// софта. Без второй попытки «стим» остаётся просто словом — steam.exe по нему не ищется.</summary>
    private static bool TryOpen(IReadOnlyList<string> candidates, out string name)
    {
        foreach (var candidate in candidates)
        {
            if (WinCtl.Launch(candidate))
            {
                name = candidate;
                return true;
            }

            if (AppsIndex.Resolve(candidate) is not { } hit) continue;
            try
            {
                hit.Run();
                name = hit.Name;
                return true;
            }
            catch (Exception)
            {
                // пробуем следующий кандидат
            }
        }

        name = string.Empty;
        return false;
    }

    private static bool LooksLikeAddress(string text) =>
        Regex.IsMatch(text, @"^[а-яa-z0-9\-]+\.[a-z]{2,}(/.*)?$", RegexOptions.IgnoreCase) || text.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private static int MatchPercent(string text)
    {
        var match = Regex.Match(text, @"(\d{1,3})");
        if (match.Success)
        {
            var value = int.Parse(match.Groups[1].Value);
            if (value is >= 0 and <= 100) return value;
        }

        // Со словарём Vosk выдаёт числа словами: «громкость сорок» → 40.
        return Vocabulary.Percent(text);
    }

    private static bool MatchesAny(string text, string[] needles) => needles.Any(text.Contains);

    private string Reply(string text)
    {
        LastReply = text;
        Understood = true;
        if (SpeakReplies) voice.Say(text);
        return text;
    }

    /// <summary>Фраза не похожа ни на одну команду: ответ не озвучиваем и не запоминаем —
    /// возможно, свободная модель разобрала её лучше.</summary>
    private string Miss(string text) => text;

    /// <summary>Ответ пришёл не из словаря команд (например, от нейросети): «повтори»
    /// должен повторить и его.</summary>
    public void Remember(string text) => LastReply = text;
}
