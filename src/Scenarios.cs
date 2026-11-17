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
        Scenario? best = null;
        foreach (var scenario in list)
        {
            var name = Commands.Normalize(scenario.Name);
            if (name.Length == 0 || !text.Contains(name, StringComparison.Ordinal)) continue;
            if (best is null || name.Length > Commands.Normalize(best.Name).Length) best = scenario;
        }

        return best;
    }
}
