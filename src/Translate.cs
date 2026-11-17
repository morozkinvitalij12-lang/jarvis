using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis;

/// <summary>Перевод через MyMemory: открытая строка запроса, без ключей и регистрации.
/// Направление берётся из фразы («на английский»), а если её нет — из алфавита самого текста.</summary>
internal static class Translate
{
    /// <summary>Экранные строки уходят пачкой, и на несколько HTTP/1.1-соединений google отвечает
    /// 429. HTTP/2 держит весь пакет одним соединением, поэтому у экрана свой клиент.</summary>
    private static readonly HttpClient Screen = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };
    private static readonly (string Needle, string Code)[] Tongues =
    [
        ("английск", "en"), ("аглийск", "en"), ("англ", "en"), ("english", "en"),
        ("русск", "ru"), ("russian", "ru"),
        ("украинск", "uk"), ("немецк", "de"), ("нем", "de"), ("german", "de"),
        ("французск", "fr"), ("франц", "fr"), ("french", "fr"),
        ("испанск", "es"), ("итальянск", "it"), ("китайск", "zh"), ("турецк", "tr"),
        ("японск", "ja"), ("корейск", "ko"), ("польск", "pl"), ("арабск", "ar")
    ];

    public static string? To(string phrase)
    {
        string? target = null;
        foreach (var (needle, code) in Tongues)
        {
            if (!phrase.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            target = code;
            break;
        }

        var text = Regex.Replace(phrase, @"\b(переведи|переведите|перевести|перевод|как будет|на язык|язык)\b", " ");
        if (target is not null) text = Regex.Replace(text, @"\b(на|в)\s+\S+", " ");
        text = Regex.Replace(text, @"\s{2,}", " ").Trim();
        if (text.Length == 0 || text.Length > 480) return null;

        var from = text.Any(c => c is >= 'а' and <= 'я' or 'ё') ? "ru" : "en";
        var to = target ?? (from == "ru" ? "en" : "ru");
        if (to == from) to = from == "ru" ? "en" : "ru";

        var json = Net.Get($"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={from}|{to}");
        if (json is null) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var result = document.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString() ?? string.Empty;
            if (result.Length == 0 || result.Contains("MYMEMORY", StringComparison.OrdinalIgnoreCase)) return null;
            return $"{text} — это {result}.";
        }
        catch (Exception e)
        {
            Console.WriteLine($"[перевод] {e.Message}");
            return null;
        }
    }

    /// <summary>Одна строка для перевода экрана. MyMemory на десятки параллельных запросов
    /// отвечает отказом, поэтому строки идут через открытый gtx-эндпоинт без ключа.</summary>
    public static string? Line(string text, string target)
    {
        if (text.Length == 0) return null;
        // Слишком длинная строка не влезает в GET-запрос: обрываем по последнему пробелу,
        // иначе переводчик получает слово посередине и портит конец строки.
        var query = text;
        if (query.Length > 1800)
        {
            var space = query.LastIndexOf(' ', 1800);
            query = query[..(space > 900 ? space : 1800)];
        }
        string json;
        try
        {
            json = Screen.GetStringAsync($"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={Uri.EscapeDataString(target)}&dt=t&q={Uri.EscapeDataString(query)}")
                .GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[перевод экрана] {e.Message}");
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0) return null;

            var translated = new StringBuilder();
            foreach (var chunk in document.RootElement[0].EnumerateArray())
                if (chunk.ValueKind == JsonValueKind.Array && chunk.GetArrayLength() > 0 && chunk[0].ValueKind == JsonValueKind.String)
                    translated.Append(chunk[0].GetString());

            var result = translated.ToString().Trim();
            return result.Length == 0 ? null : result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
