using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace Jarvis;

internal readonly record struct AppHit(string Name, string File, string Arguments)
{
    public void Run()
    {
        if (Guard.DryRun) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(File)
        {
            UseShellExecute = true,
            Arguments = Arguments
        });
    }
}

/// <summary>Что реально установлено: ярлыки меню «Пуск» и реестровые App Paths. Нужен,
/// потому что распознаватель выдаёт «стим», а на диске — steam.exe, и просто так такая
/// команда не запускается.</summary>
internal static class AppsIndex
{
    private static readonly object Sync = new();
    private static List<AppHit>? _cache;

    private static readonly string[] Skip =
    [
        "uninstall", "удалить", "удаление", "website", "site", "сайт", "forum", "читай",
        "readme", "license", "лицензия", "документация", "documentation", "help", "помощь", "support"
    ];

    public static List<AppHit> All()
    {
        lock (Sync)
        {
            if (_cache is not null) return _cache;
            var list = new List<AppHit>();
            list.AddRange(Shortcuts());
            list.AddRange(AppPaths());
            _cache = list;
            return _cache;
        }
    }

    /// <summary>Самое близкое имя программы под сказанное; null — если похожего нет.</summary>
    public static AppHit? Resolve(string spoken)
    {
        var key = Latin(spoken);
        if (key.Length < 2) return null;

        AppHit? best = null;
        var bestScore = MaxScore + 1;
        foreach (var hit in All())
        {
            var score = Score(hit.Name, key);
            if (score > bestScore) continue;
            bestScore = score;
            best = hit;
        }

        return bestScore <= MaxScore ? best : null;
    }

    private const int MaxScore = 5;

    /// <summary>0 — имя совпало буква в букву, дальше по убыванию точности. Гласные
    /// не в счёт: распознаватель даёт «стим», на диске steam, а «тлаунчер» и TLauncher
    /// сходятся именно согласными.</summary>
    private static int Score(string rawName, string key)
    {
        var name = Latin(rawName);
        if (name.Length < 2 || key.Length < 2) return int.MaxValue;
        if (name == key) return 0;

        var distance = Distance(name, key);
        var slack = Math.Min(name.Length, key.Length) >= 5 ? 2 : 1;
        var best = int.MaxValue;

        if (name.StartsWith(key, StringComparison.Ordinal) || key.StartsWith(name, StringComparison.Ordinal))
            best = 1 + Math.Min(distance, slack);

        if (key.Length >= 4)
        {
            var wanted = Skeleton(key);
            if (wanted.Length >= 3)
                foreach (var word in Words(rawName))
                {
                    var latin = Latin(word);
                    var skeleton = Skeleton(latin);
                    if (skeleton.Length < 3) continue;
                    if (skeleton == wanted) return 2;
                    // один пропущенный согласный — но только когда слово и так близко
                    // и начинается на ту же букву: иначе «тулба» уезжает в Tools.
                    if (Distance(skeleton, wanted) <= 1 && Distance(latin, key) <= 2 && latin[0] == key[0])
                        best = Math.Min(best, 4);
                    if (skeleton.Length >= wanted.Length + 1 && skeleton.StartsWith(wanted) && Distance(latin, key) <= 3)
                        best = Math.Min(best, 3);
                }
        }

        if (distance <= slack) best = Math.Min(best, 3 + distance);
        return best;
    }

    private static IEnumerable<string> Words(string rawName)
    {
        yield return rawName;
        foreach (var word in rawName.Split(new[] { ' ', '-', '_', '.', '(' }, StringSplitOptions.RemoveEmptyEntries))
            yield return word;
    }

    private static string Skeleton(string latin)
    {
        var builder = new System.Text.StringBuilder(latin.Length);
        foreach (var c in latin)
        {
            if (!char.IsBetween(c, 'a', 'z') && !char.IsBetween(c, '0', '9')) continue;
            if ("aeiouy".IndexOf(c) >= 0) continue;
            builder.Append(c switch
            {
                'c' or 'q' => 'k',
                'z' => 's',
                'v' => 'f',
                _ => c
            });
        }

        return builder.ToString();
    }

    private static IEnumerable<AppHit> Shortcuts()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + "\\Programs",
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + "\\Programs"
        };

        var shell = Shell();
        foreach (var root in roots.Where(r => r.Length > 0 && Directory.Exists(r)).Distinct())
        {
            var files = SafeEnumerate(root);
            foreach (var file in files)
            {
                var label = Path.GetFileNameWithoutExtension(file);
                if (Skip.Any(s => label.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                if (shell is null) continue;
                var (target, args) = ShortcutTarget(shell, file);
                if (target.Length == 0 || !File.Exists(target)) continue;
                yield return new AppHit(label, target, args);
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IEnumerable<AppHit> AppPaths()
    {
        var keys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"
        };

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var key in keys)
        {
            using var root = SafeOpen(hive, key);
            if (root is null) continue;
            foreach (var name in SafeSubKeyNames(root))
            {
                using var sub = SafeOpenKey(root, name);
                var value = sub?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(value)) continue;
                var file = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                if (!File.Exists(file)) continue;
                yield return new AppHit(Path.GetFileNameWithoutExtension(name), file, string.Empty);
            }
        }
    }

    private static RegistryKey? SafeOpen(RegistryKey hive, string name)
    {
        try
        {
            return hive.OpenSubKey(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string[] SafeSubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static RegistryKey? SafeOpenKey(RegistryKey parent, string name)
    {
        try
        {
            return parent.OpenSubKey(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? Shell()
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            return type is null ? null : Activator.CreateInstance(type);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (string Target, string Arguments) ShortcutTarget(object shell, string link)
    {
        try
        {
            var item = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [link]);
            if (item is null) return (string.Empty, string.Empty);
            var target = item.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, item, null) as string;
            var args = item.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, item, null) as string;
            return (target ?? string.Empty, args ?? string.Empty);
        }
        catch (Exception)
        {
            return (string.Empty, string.Empty);
        }
    }

    private static readonly Dictionary<char, string> Letters = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya"
    };

    /// <summary>Одно и то же название по-русски и по-английски должно совпасть:
    /// «стим» и steam оба дают steam.</summary>
    public static string Latin(string text)
    {
        var lower = text.ToLowerInvariant();
        var builder = new System.Text.StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            if (Letters.TryGetValue(c, out var mapped))
            {
                builder.Append(mapped);
                continue;
            }

            if (char.IsBetween(c, 'a', 'z') || char.IsBetween(c, '0', '9')) builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Расстояние Левенштейна с порогом: дальше считать нет смысла.</summary>
    private static int Distance(string a, string b)
    {
        const int cap = 8;
        if (Math.Abs(a.Length - b.Length) > cap) return cap;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                rowBest = Math.Min(rowBest, current[j]);
            }

            if (rowBest > cap) return cap;
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
