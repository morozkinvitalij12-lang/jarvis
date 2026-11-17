using System.Globalization;
using System.Text.Json;

namespace Jarvis;

/// <summary>Курсы со страницы Центробанка: тот же открытый источник, что и в виджетах, ключ
/// не нужен. Ответ — рубли за одну единицу валюты, как в новостях.</summary>
internal static class Money
{
    private static readonly (string Code, string[] Words)[] Coins =
    [
        ("USD", ["доллар", "доллара", "долларов", "американ", "usd"]),
        ("EUR", ["евро", "euro"]),
        ("CNY", ["юан", "китайск", "cny"]),
        ("GBP", ["фунт", "стерлинг", "британ", "gbp"]),
        ("TRY", ["лир", "турецк", "try"]),
        ("KZT", ["тенге", "казах", "kzt"]),
        ("UAH", ["гривн", "украин", "uah"]),
        ("BYN", ["белорус", "byn"]),
        ("INR", ["руп", "индийск", "inr"]),
        ("PLN", ["злот", "польск", "pln"]),
    ];

    public static string? Report(string phrase)
    {
        var json = Net.Get("https://www.cbr-xml-daily.ru/daily_json.js");
        if (json is null) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var values = document.RootElement.GetProperty("Valute");
            var code = Pick(phrase);

            if (code is not null && values.TryGetProperty(code, out var one)) return Line(one);

            var list = new List<string>();
            foreach (var wanted in new[] { "USD", "EUR", "CNY" })
                if (values.TryGetProperty(wanted, out var item)) list.Add(Short(item));

            return list.Count == 0 ? null : $"Сегодня {string.Join(", ", list)}.";
        }
        catch (Exception e)
        {
            Console.WriteLine($"[валюты] {e.Message}");
            return null;
        }
    }

    private static string? Pick(string phrase)
    {
        var text = phrase.ToLowerInvariant().Replace('ё', 'е');
        foreach (var (code, words) in Coins)
            if (words.Any(w => text.Contains(w, StringComparison.Ordinal))) return code;
        return null;
    }

    private static string Line(JsonElement item)
    {
        var name = Text(item, "Name");
        var (rubles, kopecks) = PerUnit(item, "Value");
        var delta = Difference(item);
        return $"{name}: {rubles} {Weather.Plural(rubles, "рубль", "рубля", "рублей")} {kopecks} " +
               $"{Weather.Plural(kopecks, "копейка", "копейки", "копеек")}.{delta}";
    }

    /// <summary>Краткая строка для сводки: «доллар 82 рубля 15 копеек» — синтезатор читает
    /// такое без «восемьдесят две целых пятнадцать сотых».</summary>
    private static string Short(JsonElement item)
    {
        var (rubles, kopecks) = PerUnit(item, "Value");
        var name = Text(item, "Name").Split(' ')[0].ToLowerInvariant();
        return $"{name} {rubles} {Weather.Plural(rubles, "рубль", "рубля", "рублей")}" +
               (kopecks == 0 ? string.Empty : $" {kopecks} {Weather.Plural(kopecks, "копейка", "копейки", "копеек")}");
    }

    private static string Difference(JsonElement item)
    {
        var before = Number(item, "Previous");
        var now = Number(item, "Value");
        var nominal = Nominal(item);
        if (before <= 0 || nominal <= 0 || Math.Abs(now - before) < 0.005) return " Без изменений.";

        var cents = (int)Math.Round(Math.Abs(now - before) / nominal * 100);
        if (cents == 0) return string.Empty;
        var where = now > before ? "вырос" : "упал";
        return $" За сутки {where} на {cents} {Weather.Plural(cents, "копейка", "копейки", "копеек")}.";
    }

    private static (int Rubles, int Kopecks) PerUnit(JsonElement item, string property)
    {
        var per = Number(item, property) / Math.Max(1, Nominal(item));
        var rubles = (int)Math.Floor(per);
        return (rubles, (int)Math.Round((per - rubles) * 100));
    }

    private static int Nominal(JsonElement item) =>
        item.TryGetProperty("Nominal", out var value) && value.TryGetInt32(out var parsed) ? parsed : 1;

    private static double Number(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value)
        && double.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static string Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;
}
