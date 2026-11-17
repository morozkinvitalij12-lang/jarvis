namespace Jarvis;

/// <summary>Однострочный журнал падений рядом с exe: без него ошибка фонового потока уходит молча.</summary>
internal static class Log
{
    private static readonly object _gate = new();
    private static readonly string _file = Path.Combine(AppContext.BaseDirectory, "jarvis-crash.log");

    public static void Line(string message)
    {
        lock (_gate)
        {
            try
            {
                File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // Журнал не должен мешать работе помощника.
            }
        }
    }
}
