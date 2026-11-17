using System.Globalization;
using System.Text.Json;

namespace Jarvis;

/// <summary>Погода с wttr.in: бесплатный источник без ключей и регистрации, город прямо
/// в адресе. Без интернета помощник честно говорит, что не связался с сервисом.</summary>
internal static class Weather
{
    private static readonly Dictionary<string, string> Sky = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sunny"] = "ясно",
        ["clear"] = "ясно",
        ["partly cloudy"] = "переменная облачность",
        ["cloudy"] = "облачно",
        ["overcast"] = "пасмурно",
        ["mist"] = "туман",
        ["fog"] = "туман",
        ["patchy rain possible"] = "возможен дождь",
        ["patchy rain nearby"] = "рядом дождь",
        ["patchy light drizzle"] = "морось",
        ["light rain"] = "слабый дождь",
        ["light rain shower"] = "кратковременный дождь",
        ["moderate rain"] = "дождь",
        ["moderate rain at times"] = "периодический дождь",
        ["heavy rain"] = "сильный дождь",
        ["patchy snow possible"] = "возможен снег",
        ["light snow"] = "небольшой снег",
        ["moderate snow"] = "снег",
        ["heavy snow"] = "сильный снег",
        ["blizzard"] = "метель",
        ["thundery outbreaks possible"] = "возможна гроза",
        ["patchy light rain with thunder"] = "гроза с дождём"
    };

    /// <summary>Слышим город в падеже («в казани», «в санкт-петербурге»), а wttr ищет по
    /// именительному и по склонённой форме находит чужие места. Поэтому город сводится к
    /// основе, а основа — к известному названию; незнакомые уходят как есть.</summary>
    private static readonly (string Needle, string Name)[] Places =
    [
        ("москв", "Москва"), ("петербург", "Санкт-Петербург"), ("питер", "Санкт-Петербург"),
        ("казан", "Казань"), ("уф", "Уфа"), ("перм", "Пермь"), ("самар", "Самара"),
        ("саратов", "Саратов"), ("воронеж", "Воронеж"), ("тул", "Тула"), ("твер", "Тверь"),
        ("ярославл", "Ярославль"), ("владимир", "Владимир"), ("калининград", "Калининград"),
        ("екатеринбург", "Екатеринбург"), ("челябинск", "Челябинск"), ("новосибирск", "Новосибирск"),
        ("омск", "Омск"), ("тюмен", "Тюмень"), ("тольятти", "Тольятти"), ("ижевск", "Ижевск"),
        ("красноярск", "Красноярск"), ("хабаровск", "Хабаровск"), ("владивосток", "Владивосток"),
        ("краснодар", "Краснодар"), ("ростов", "Ростов-на-Дону"), ("ставропол", "Ставрополь"),
        ("новгород", "Великий Новгород"), ("минск", "Минск"), ("гомел", "Гомель"),
        ("киев", "Киев"), ("львов", "Львов"), ("одесс", "Одесса"), ("харьков", "Харьков"),
        ("алматы", "Алматы"), ("астан", "Астана"), ("ашхабад", "Ашхабад"), ("ташкент", "Ташкент"),
        ("бишкек", "Бишкек"), ("сочи", "Сочи"),
    ];

    public static string? Report(string city, string fallback)
    {
        var place = city.Trim().Length > 0 ? city.Trim() : fallback.Trim();
        if (place.Length == 0) place = "Москва";
        var lowered = place.ToLowerInvariant();
        var known = string.Empty;
        foreach (var (needle, name) in Places)
        {
            if (!lowered.Contains(needle, StringComparison.Ordinal)) continue;
            place = name;
            known = name;
            break;
        }

        var json = Net.Get($"https://wttr.in/{Uri.EscapeDataString(place)}?format=j1&lang=ru");
        if (json is null) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var now = root.GetProperty("current_condition")[0];
            // Для известного города всегда называем его сами: wttr подписывает
            // Санкт-Петербург как «New Holland», и вслух это звучит как ошибка.
            var name = known.Length > 0 ? known : Name(root, place);
            var temp = Read(now.GetProperty("temp_C").GetString());
            var feels = Read(now.GetProperty("FeelsLikeC").GetString());
            var wind = Read(now.GetProperty("windspeedKmph").GetString());

            var weather = Describe(now);
            return $"{name}: {weather}, {Degrees(temp)}. Ощущается как {Degrees(feels)}, ветер {wind} {Plural(wind, "километр", "километра", "километров")} в час.";
        }
        catch (Exception e)
        {
            Console.WriteLine($"[погода] {e.Message}");
            return null;
        }
    }

    /// <summary>С lang=ru описание лежит в отдельном массиве lang_ru, а weatherDesc
    /// остаётся английским — берём русское, иначе в ответе смеси языков.</summary>
    private static string Describe(JsonElement now)
    {
        var raw = string.Empty;
        if (now.TryGetProperty("lang_ru", out var russian) && russian[0].TryGetProperty("value", out var ru))
            raw = ru.GetString()?.Trim() ?? string.Empty;
        if (raw.Length == 0) raw = now.GetProperty("weatherDesc")[0].GetProperty("value").GetString()?.Trim() ?? string.Empty;

        return Sky.TryGetValue(raw, out var known) ? known : raw.ToLowerInvariant();
    }

    private static string Name(JsonElement root, string asked)
    {
        try
        {
            return root.GetProperty("nearest_area")[0].GetProperty("areaName")[0].GetProperty("value").GetString() is { Length: > 0 } found
                ? found
                : asked;
        }
        catch (Exception)
        {
            return asked;
        }
    }

    private static int Read(string? text) =>
        int.TryParse((text ?? string.Empty).Replace("+", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static string Degrees(int value) => value < 0
        ? $"минус {Math.Abs(value)} {Plural(Math.Abs(value), "градус", "градуса", "градусов")}"
        : $"{value} {Plural(value, "градус", "градуса", "градусов")}";

    /// <summary>Русская плюральность: синтезатор читает цифры вслух, и «2 километр» звучит поломано.</summary>
    internal static string Plural(int value, string one, string few, string many)
    {
        var tail = Math.Abs(value) % 100;
        if (tail is >= 11 and <= 14) return many;
        return (tail % 10) switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many
        };
    }
}
