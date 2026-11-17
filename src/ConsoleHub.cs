using System.Text;

namespace Jarvis;

/// <summary>Журнал помощника для окна: строки, которые раньше шли только в консоль,
/// теперь видны на вкладке «Пульт». Консоль при этом остаётся консолью для режимов
/// вроде --selftest.</summary>
internal static class ConsoleHub
{
    private static readonly object Gate = new();
    private static bool _attached;

    public static event Action<string>? OnLine;

    /// <summary>Подключает перехват. sink = null означает «консоль больше не нужна».</summary>
    public static void Attach(TextWriter? sink)
    {
        lock (Gate)
        {
            if (_attached) return;
            _attached = true;
            var tee = new Tee(sink ?? Console.Out);
            Console.SetOut(tee);
            Console.SetError(tee);
        }
    }

    private static void Emit(string line)
    {
        OnLine?.Invoke(line);
    }

    private sealed class Tee(TextWriter inner) : TextWriter
    {
        private readonly StringBuilder _pending = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            Safe(() => inner.Write(value));
            Feed(value);
        }

        public override void Write(string? value)
        {
            Safe(() => inner.Write(value));
            if (value is null) return;
            foreach (var ch in value) Feed(ch);
        }

        public override void WriteLine()
        {
            Safe(inner.WriteLine);
            Feed('\n');
        }

        public override void WriteLine(string? value)
        {
            Safe(() => inner.WriteLine(value));
            Feed('\n');
        }

        public override void Flush() => Safe(inner.Flush);

        private static void Safe(Action write)
        {
            // Консоль может быть отвязана (FreeConsole) — журнал окна важнее мёртвого handle.
            try
            {
                write();
            }
            catch (Exception)
            {
            }
        }

        private void Feed(char value)
        {
            if (value is not ('\n' or '\r'))
            {
                _pending.Append(value);
                return;
            }

            if (_pending.Length == 0) return;
            var line = _pending.ToString();
            _pending.Clear();
            Emit(line);
        }
    }
}
