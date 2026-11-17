using System.Text;

namespace Jarvis;

/// <summary>Сайты по сказанному имени. «Открой ютуб» — не адрес и не программа, и без такой
/// таблицы помощнику остаётся только «не нашёл». Свои имена добавляются в jarvis.json в
/// разделе sites и перекрывают встроенные: имя из конфига выигрывает, адрес можно без схемы.</summary>
internal static class Sites
{
    private static readonly Dictionary<string, string> BuiltIn = new(StringComparer.Ordinal)
    {
        ["youtube"] = "https://youtube.com",
        ["ютуб"] = "https://youtube.com",
        ["ютуб музыка"] = "https://music.youtube.com",
        ["вконтакте"] = "https://vk.com",
        ["vk"] = "https://vk.com",
        ["одноклассники"] = "https://ok.ru",
        ["ок"] = "https://ok.ru",
        ["авито"] = "https://avito.ru",
        ["яндекс"] = "https://yandex.ru",
        ["переводчик"] = "https://translate.yandex.ru",
        ["почта"] = "https://mail.yandex.ru",
        ["маркет"] = "https://market.yandex.ru",
        ["дзен"] = "https://dzen.ru",
        ["rutube"] = "https://rutube.ru",
        ["рутуб"] = "https://rutube.ru",
        ["google"] = "https://google.com",
        ["гугл"] = "https://google.com",
        ["википедия"] = "https://ru.wikipedia.org",
        ["вики"] = "https://ru.wikipedia.org",
        ["habr"] = "https://habr.com",
        ["хабр"] = "https://habr.com",
        ["пикабу"] = "https://pikabu.ru",
        ["reddit"] = "https://reddit.com",
        ["реддит"] = "https://reddit.com",
        ["twitter"] = "https://twitter.com",
        ["твиттер"] = "https://twitter.com",
        ["twitch"] = "https://twitch.tv",
        ["твич"] = "https://twitch.tv",
        ["discord"] = "https://discord.com",
        ["дискорд"] = "https://discord.com",
        ["telegram"] = "https://web.telegram.org",
        ["телеграм"] = "https://web.telegram.org",
        ["whatsapp"] = "https://web.whatsapp.com",
        ["steam"] = "https://store.steampowered.com",
        ["стим"] = "https://store.steampowered.com",
        ["epic"] = "https://store.epicgames.com",
        ["эпик"] = "https://store.epicgames.com",
        ["netflix"] = "https://netflix.com",
        ["нетфликс"] = "https://netflix.com",
        ["кинопоиск"] = "https://www.kinopoisk.ru",
        ["ozon"] = "https://www.ozon.ru",
        ["озон"] = "https://www.ozon.ru",
        ["wildberries"] = "https://www.wildberries.ru",
        ["вайлдберриз"] = "https://www.wildberries.ru",
        ["aliexpress"] = "https://aliexpress.ru",
        ["алиэкспресс"] = "https://aliexpress.ru",
        ["gosuslugi"] = "https://www.gosuslugi.ru",
        ["госуслуги"] = "https://www.gosuslugi.ru",
        ["github"] = "https://github.com",
        ["гитхаб"] = "https://github.com",
        ["stackoverflow"] = "https://stackoverflow.com",
    };

    /// <summary>Ища по алфавиту, чтобы список можно было показать человеку: ключ — как говорят,
    /// значение — куда идём.</summary>
    public static IEnumerable<KeyValuePair<string, string>> All(IReadOnlyDictionary<string, string>? custom)
    {
        var merged = new Dictionary<string, string>(BuiltIn, StringComparer.Ordinal);
        if (custom is not null)
            foreach (var (key, url) in custom)
                if (Normalize(key).Length > 0) merged[Normalize(key)] = url;
        return merged.OrderBy(p => p.Key, StringComparer.Ordinal);
    }

    public static string? Resolve(string spoken, IReadOnlyDictionary<string, string>? custom)
    {
        var text = Normalize(spoken);
        if (text.Length == 0) return null;
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Распознаватель рвёт непривычное слово на части: «ю туб». Склейка через пробел
        // возвращает его к имени, а короткие имена по-прежнему ловятся только целиком.
        var glued = text.Replace(" ", string.Empty);

        // Имя пользователя важнее встроенного, а длинное имя важнее короткого:
        // «ютуб музыка» — это не «ютуб».
        var candidates = (custom ?? new Dictionary<string, string>())
            .Select(p => (Key: Normalize(p.Key), Url: p.Value))
            .Concat(BuiltIn.Select(p => (Key: p.Key, Url: p.Value)))
            .Where(c => c.Key.Length > 0)
            .OrderByDescending(c => c.Key.Length);

        foreach (var (key, url) in candidates)
            if (Matches(key, text, tokens, glued)) return Address(url);

        return null;
    }

    private static bool Matches(string key, string text, string[] tokens, string glued) =>
        key == text || key == glued || tokens.Contains(key, StringComparer.Ordinal)
        || key.Length >= 4 && (text.Contains(key, StringComparison.Ordinal) || glued.Contains(key, StringComparison.Ordinal));

    /// <summary>Распознаватель приносит «ютуб точка ком» и «гит хабе»: точки, дефисы и слэши
    /// сводим к пробелам, ё к е, остальное — только буквы и цифры.</summary>
    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            var ch = character == 'ё' ? 'е' : character;
            if (ch is >= 'а' and <= 'я' or >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(ch);
            else if (ch == ' ' || ch is '.' or '-' or '_' or '/') builder.Append(' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Address(string url) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || url.Contains("://") ? url : "https://" + url;
}
