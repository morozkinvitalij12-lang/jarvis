using System.Text;

namespace Jarvis;

internal sealed class Dictation(Config cfg)
{
    private readonly StringBuilder _buffer = new();

    public bool Active { get; private set; }

    public int Words => _buffer.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    public void Begin()
    {
        Active = true;
        _buffer.Clear();
    }

    public void Append(string text)
    {
        if (!Active) return;
        if (_buffer.Length > 0) _buffer.Append(' ');
        _buffer.Append(text.Trim());
    }

    public string Cancel()
    {
        Active = false;
        var discarded = _buffer.ToString();
        _buffer.Clear();
        return discarded;
    }

    public string Save()
    {
        Active = false;
        var text = _buffer.ToString().Trim();
        _buffer.Clear();
        if (text.Length == 0) return string.Empty;

        Directory.CreateDirectory(cfg.DictationDir);
        var path = Path.Combine(cfg.DictationDir, $"диктовка-{DateTime.Now:yyyy-MM-dd}.txt");
        File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss}\n{text}\n\n", System.Text.Encoding.UTF8);
        return path;
    }
}
