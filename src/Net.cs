using System.Net.Http;

namespace Jarvis;

/// <summary>Сетевые запросы помощника: один клиент на все сервисы и короткое окно ожидания.
/// Голосовой помощник не должен молчать полминуты, пока сервер «думает».</summary>
internal static class Net
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static string? Get(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Jarvis/1.0");
            using var response = Http.Send(request);
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[сеть] {Host(url)}: {e.Message}");
            return null;
        }
    }

    private static string Host(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch (UriFormatException)
        {
            return url;
        }
    }
}
