using System.Text.Json.Serialization;

namespace Jarvis;

public sealed class Scenario
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("steps")] public string[] Steps { get; set; } = [];
}

/// <summary>Сценарии — цепочки обычных команд, которые помощник выполняет подряд.
/// Хранятся в jarvis.json, поэтому свой набор правится текстом без пересборки.</summary>
internal static class Scenarios
{
    public static Scenario? Find(IEnumerable<Scenario> list, string phrase)
    {
        var text = Commands.Normalize(phrase);
        var glued = Glue(text);
        Scenario? best = null;
        foreach (var scenario in list)
        {
            var name = Commands.Normalize(scenario.Name);
            if (name.Length == 0) continue;

            // Распознаватель ставит пробел там, где человек сказал слово слитно («майн крафт»),
            // и наоборот, поэтому имя сверяется и по обычному тексту, и по склейке без пробелов.
            var hit = text.Contains(name, StringComparison.Ordinal) || glued.Contains(Glue(name));
            if (!hit) continue;
            if (best is null || name.Length > Commands.Normalize(best.Name).Length) best = scenario;
        }

        return best;
    }

    private static string Glue(string text) => string.Concat(text.Where(ch => !char.IsWhiteSpace(ch)));
}
