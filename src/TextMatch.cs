namespace Jarvis;

internal static class TextMatch
{
    public static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    public static bool ContainsWake(string text, string[] wakeWords)
    {
        var normalized = Commands.Normalize(text);
        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        foreach (var wake in wakeWords)
        {
            var target = Commands.Normalize(wake);
            if (token == target) return true;
            var allowed = target.Length >= 6 ? 2 : 1;
            if (token.Length >= 3 && Distance(token, target) <= allowed) return true;
        }

        return false;
    }

    public static string StripWake(string text, string[] wakeWords)
    {
        var normalized = Commands.Normalize(text);
        var kept = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !wakeWords.Any(wake => Distance(token, Commands.Normalize(wake)) <= (Commands.Normalize(wake).Length >= 6 ? 2 : 1)));
        return string.Join(' ', kept);
    }
}

internal static class Phrases
{
    public static readonly string[] Prewarm =
    [
        "Системы запущены. Слушаю вас.",
        "Не слышу команд.",
        "Не понял команду.",
        "Приветствую. Чем могу быть полезен?",
        "Все системы в норме.",
        "Всегда к вашим услугам.",
        "Отключаюсь. До связи.",
        "Перехожу в режим ожидания. Назовите моё имя, когда понадобится.",
        "Голос включён.",
        "Следующий трек.",
        "Предыдущий трек.",
        "Музыка на паузе.",
        "Продолжаю.",
        "Звук отключён.",
        "Звук включён.",
        "Громкость увеличена.",
        "Громкость уменьшена.",
        "Нечего закрывать.",
        "Сворачиваю.",
        "Разворачиваю.",
        "Диктую. Скажите «сохраняю» или «отменяю».",
        "Отменяю запись.",
        "Печатаю.",
        "Сохраняю.",
        "Открываю.",
        "Ищу в интернете.",
        "Секунду, уточняю.",
        "Не слышу свою нейросеть: похоже, Ollama не запущена.",
        "Нейросеть не успела ответить. Попробуйте ещё раз чуть позже.",
    ];
}
