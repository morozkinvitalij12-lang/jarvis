using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis;

internal enum BrainStatus
{
    /// <summary>Ответ получен и готов к озвучке.</summary>
    Answered,
    /// <summary>Спрашивать нечего или не время: помощник отвечает как раньше.</summary>
    Skipped,
    /// <summary>Ollama не отвечает: сервис не запущен или адрес недоступен.</summary>
    Unreachable,
    /// <summary>Сервис на месте, но запрошенной модели нет.</summary>
    NoModel,
    /// <summary>Модель думала дольше отведённого окна.</summary>
    TimedOut,
    /// <summary>Ответ пустой или разобрать его не удалось.</summary>
    Failed,
}

/// <summary>Умные ответы: локальная нейросеть Qwen через Ollama — без облаков и без ключей.
/// Подключается только там, где команды помощника не поняли фразу, и не роняет сессию:
/// вместо этого вежливое сообщение в стиле Джарвиса и запись в журнал.</summary>
internal static class Brain
{
    private const int MaxSpeechChars = 420;

    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>После «сервиса нет» не дёргаем его на каждой фразе: иначе на непонятных словах
    /// помощник тормозит и повторяет одно и то же.</summary>
    private static DateTime _retryAfter = DateTime.MinValue;

    private const string Style = """
        Ты русскоязычный помощник. Ты ОБЯЗАН отвечать ТОЛЬКО на русском языке.
        Никакого китайского, английского или другого языка — даже если вопрос на другом языке.
        Ответ: одно-два коротких предложения, простым разговорным текстом.
        Без markdown, без списков, без эмодзи и без звёздочек — ответ будут произносить вслух.
        Если не знаешь ответ, честно скажи об этом одним предложением на русском.
        """;

    public static async Task<(BrainStatus Status, string Reply)> AskAsync(Config cfg, string question,
        CancellationToken cancel = default, Action? onAttempt = null)
    {
        var trimmed = question.Trim();
        if (!cfg.SmartAnswers || trimmed.Length == 0 || Guard.DryRun || DateTime.Now < _retryAfter)
            return (BrainStatus.Skipped, string.Empty);

        onAttempt?.Invoke();
        var payload = JsonSerializer.Serialize(new
        {
            model = cfg.OllamaModel,
            stream = false,
            // «Думающие» модели (qwen3) иначе съедают весь лимит токенов скрытым рассуждением
            // и присылают пустой ответ, который нечего озвучивать. Старым моделям поле безразлично.
            think = false,
            options = new { num_predict = 240, temperature = 0.6, num_ctx = 2048 },
            messages = new object[]
            {
                new { role = "system", content = Style },
                new { role = "user", content = trimmed },
            },
        });

        var attempt = await SendAsync(cfg, "/api/chat", payload, cancel);
        if (attempt.Status == BrainStatus.Answered) return (BrainStatus.Answered, attempt.Text);

        // Таймаут — это обычно «модель ещё грузится»: даём человеку попробовать снова,
        // но не раньше, чем через полминуты.
        _retryAfter = DateTime.Now + (attempt.Status == BrainStatus.TimedOut
            ? TimeSpan.FromSeconds(30)
            : TimeSpan.FromMinutes(2));
        return (attempt.Status, Phrase(attempt.Status, cfg));
    }

    /// <summary>Модель отвечает markdown'ом с переносами и списками; синтезатор читает это
    /// вслух с артефактами, поэтому оставляем одну строку обычной речи.</summary>
    public static string ForSpeech(string text)
    {
        var cleaned = Regex.Replace(text, "```[a-z0-9]*", " ", RegexOptions.IgnoreCase);
        // Маркдаун приходит списками и цитатами: знаки съедаем, переносы превращаем в точки,
        // иначе синтезатор читает «дефис короткие волны дефис».
        cleaned = Regex.Replace(cleaned, @"(?m)^\s{0,3}(?:[-*+•]\s+|\d+[.)]\s+|>\s?|#{1,6}\s*)", " ");
        cleaned = Regex.Replace(cleaned, @"[*_`|]", " ");
        cleaned = Regex.Replace(cleaned, @"\s*\r?\n\s*", ". ");
        cleaned = Regex.Replace(cleaned, @"-{2,}", " ");
        cleaned = Regex.Replace(cleaned, @"(?:\s*\.){2,}", ".").Trim();
        cleaned = Regex.Replace(cleaned, @"\s*\.\s*", ". ");
        cleaned = Regex.Replace(cleaned, @"\s+([,;:])", "$1");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        if (cleaned.Length > MaxSpeechChars)
        {
            var head = cleaned[..MaxSpeechChars];
            var stop = head.LastIndexOf(". ", StringComparison.Ordinal);
            var tail = stop > MaxSpeechChars / 3 ? stop : head.LastIndexOf(' ');
            cleaned = tail <= 0 ? head : head[..tail];
        }

        cleaned = cleaned.Trim().TrimEnd('.', ' ', ':', ';', ',', '-');
        return cleaned.Length == 0 ? string.Empty : cleaned + ".";
    }

    /// <summary>Разведка для «Jarvis.exe --brain»: жив ли сервис, та ли модель стоит.</summary>
    public static async Task<string> ReportAsync(Config cfg, string question, CancellationToken cancel = default)
    {
        if (question.Trim().Length > 0)
        {
            var answer = await AskAsync(cfg, question, cancel);
            if (answer.Status == BrainStatus.Answered) return $"ответ: {answer.Reply}";
            if (answer.Status == BrainStatus.Skipped)
                return "умные ответы сейчас не спрашиваются: выключены в настройках, в jarvis.json нет smartAnswers, " +
                       "или недавно уже был сбой (повтор через пару минут)";
            return $"{answer.Status}: {answer.Reply}";
        }

        var models = await GetTagsAsync(cfg, cancel);
        if (models is null)
            return $"Ollama по адресу {cfg.OllamaUrl} не отвечает.\n" +
                   $"Установка: winget install Ollama.Ollama, затем в консоли: ollama pull {cfg.OllamaModel}\n" +
                   $"Проверка: ollama list и {cfg.OllamaUrl}/api/tags в браузере.";

        var has = models.Any(m => m.Contains(cfg.OllamaModel, StringComparison.OrdinalIgnoreCase));
        var sb = new StringBuilder($"Ollama отвечает на {cfg.OllamaUrl}. Моделей: {models.Count}");
        sb.Append(models.Count == 0 ? " (ни одной)" : ": " + string.Join(", ", models));
        sb.Append(has
            ? $"\nНужная «{cfg.OllamaModel}» на месте."
            : $"\nНужной «{cfg.OllamaModel}» нет. Загрузите: ollama pull {cfg.OllamaModel}");
        sb.Append("\nПроверка ответа: Jarvis.exe --brain \"почему небо голубое\"");
        return sb.ToString();
    }

    private static async Task<List<string>?> GetTagsAsync(Config cfg, CancellationToken cancel)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await Http.GetAsync(Url(cfg.OllamaUrl, "/api/tags"), timeout.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (!doc.RootElement.TryGetProperty("models", out var models)) return [];
            return [.. models.EnumerateArray()
                .Where(m => m.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                .Select(m => m.GetProperty("name").GetString() ?? string.Empty)
                .Where(n => n.Length > 0)];
        }
        catch (Exception e)
        {
            Console.WriteLine($"[мозг] {cfg.OllamaUrl}: {e.Message}");
            return null;
        }
    }

    private static async Task<(BrainStatus Status, string Text)> SendAsync(Config cfg, string path,
        string payload, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(cfg.BrainTimeoutSeconds, 3, 600)));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url(cfg.OllamaUrl, path));
            request.Headers.UserAgent.ParseAdd("Jarvis/1.0");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode) return (Classify(response.StatusCode, body), string.Empty);
            var text = ForSpeech(Content(body));
            return text.Length == 0 ? (BrainStatus.Failed, string.Empty) : (BrainStatus.Answered, text);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return (BrainStatus.Skipped, string.Empty);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[мозг] {cfg.OllamaModel} молчит дольше {cfg.BrainTimeoutSeconds} с");
            return (BrainStatus.TimedOut, string.Empty);
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"[мозг] {cfg.OllamaUrl}: {e.Message}");
            return (BrainStatus.Unreachable, string.Empty);
        }
        catch (Exception e)
        {
            Log.Line($"нейросеть: {e}");
            return (BrainStatus.Failed, string.Empty);
        }
    }

    private static BrainStatus Classify(HttpStatusCode code, string body) => code switch
    {
        HttpStatusCode.NotFound when MentionsModel(body) => BrainStatus.NoModel,
        HttpStatusCode.NotFound => BrainStatus.Unreachable,
        _ => BrainStatus.Failed,
    };

    private static bool MentionsModel(string body) =>
        body.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || body.Contains("try pulling", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ответ в /api/chat лежит в message.content, в /api/generate — в response:
    /// разбираем оба, чтобы работало с любым из эндпоинтов.</summary>
    private static string Content(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
                return content.GetString() ?? string.Empty;
            return root.TryGetProperty("response", out var response) ? response.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string Phrase(BrainStatus status, Config cfg) => status switch
    {
        BrainStatus.Unreachable => "Не слышу свою нейросеть: похоже, Ollama не запущена.",
        BrainStatus.NoModel => "Модель нейросети не загружена — команду установки записал в журнал.",
        BrainStatus.TimedOut => "Нейросеть не успела ответить. Попробуйте ещё раз чуть позже.",
        BrainStatus.Failed => "Нейросеть ответила невразумительно.",
        _ => $"Нейросеть недоступна (модель «{cfg.OllamaModel}»).",
    };

    private static string Url(string root, string path) => root.TrimEnd('/') + path;
}
