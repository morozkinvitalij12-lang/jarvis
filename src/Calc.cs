using System.Globalization;
using System.Text.RegularExpressions;

namespace Jarvis;

/// <summary>Калькулятор без интернета. Распознаватель приносит числа словами
/// («сто двадцать два плюс три»), поэтому фраза сначала собирается в токены,
/// а потом считается обычным вычислителем с приоритетом.</summary>
internal static class Calc
{
    private enum Kind
    {
        Number, Add, Subtract, Multiply, Divide, Power, Root, Open, Close
    }

    private readonly record struct Token(Kind Kind, double Value);

    private static readonly Dictionary<string, double> Big = new(StringComparer.Ordinal)
    {
        ["тысяча"] = 1000, ["тысячи"] = 1000, ["тысяч"] = 1000,
        ["миллион"] = 1_000_000, ["миллиона"] = 1_000_000, ["миллионов"] = 1_000_000,
        ["миллиард"] = 1_000_000_000, ["миллиарда"] = 1_000_000_000, ["миллиардов"] = 1_000_000_000
    };

    public static string? Evaluate(string phrase)
    {
        var tokens = Tokens(phrase);
        if (tokens.Count == 0) return null;

        var at = 0;
        var value = Expression(tokens, ref at);
        if (at != tokens.Count || value is not { } result) return null;
        if (double.IsNaN(result) || double.IsInfinity(result)) return null;
        return Format(result);
    }

    private static List<Token> Tokens(string phrase)
    {
        var text = phrase.ToLowerInvariant().Replace('ё', 'е');
        text = Regex.Replace(text,
            @"\b(сколько|будет|равно|равняется|получится|посчитай|вычисли|реши|пример|калькулятор|значит|число|ответ)\b",
            " ");

        foreach (var (from, to) in Words) text = text.Replace(from, to);

        var tokens = new List<Token>();
        var running = 0d;
        var number = false;

        void Flush()
        {
            if (!number) return;
            tokens.Add(new Token(Kind.Number, running));
            running = 0;
            number = false;
        }

        foreach (Match match in Regex.Matches(text, @"[\d.,]+|[а-яa-z]+|\S"))
        {
            var raw = match.Value;
            if (char.IsDigit(raw[0]))
            {
                Flush();
                if (double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var digits))
                    tokens.Add(new Token(Kind.Number, digits));
                continue;
            }

            if (Symbols.TryGetValue(raw, out var kind))
            {
                Flush();
                tokens.Add(new Token(kind, 0));
                continue;
            }

            if (Vocabulary.Numbers.TryGetValue(raw, out var small))
            {
                running += small;
                number = true;
                continue;
            }

            if (Big.TryGetValue(raw, out var large))
            {
                running = (running == 0 ? 1 : running) * large;
                number = true;
                continue;
            }

            Flush();
        }

        Flush();
        return tokens;
    }

    private static readonly (string From, string To)[] Words =
    [
        ("умножить на", "*"), ("умножить", "*"), ("умножь", "*"), ("умножения", "*"),
        ("разделить на", "/"), ("разделить", "/"), ("раздели", "/"), ("делением", "/"),
        ("прибавить", "+"), ("плюс", "+"), ("отнять", "-"), ("минус", "-"),
        ("процентов от", "/100*"), ("процента от", "/100*"), ("процент от", "/100*"),
        ("в квадрате", "^2"), ("квадрат", "^2"), ("корень из", "@"), ("корень", "@"),
        ("открыть скобку", "("), ("закрыть скобку", ")"), ("скобки", "")
    ];

    private static readonly Dictionary<string, Kind> Symbols = new(StringComparer.Ordinal)
    {
        ["+"] = Kind.Add, ["-"] = Kind.Subtract, ["*"] = Kind.Multiply, ["/"] = Kind.Divide,
        ["^"] = Kind.Power, ["@"] = Kind.Root, ["("] = Kind.Open, [")"] = Kind.Close,
        ["x"] = Kind.Multiply, ["divide"] = Kind.Divide,
        // «сто двадцать на три» — разговорный вариант умножения, распознаватель приносит его как есть.
        ["на"] = Kind.Multiply, ["и"] = Kind.Add
    };

    private static double? Expression(IReadOnlyList<Token> tokens, ref int at)
    {
        var left = Term(tokens, ref at);
        while (left is { } value && at < tokens.Count && tokens[at].Kind is Kind.Add or Kind.Subtract)
        {
            var op = tokens[at].Kind;
            at++;
            if (Term(tokens, ref at) is not { } right) return null;
            left = op == Kind.Add ? value + right : value - right;
        }

        return left;
    }

    private static double? Term(IReadOnlyList<Token> tokens, ref int at)
    {
        var left = Factor(tokens, ref at);
        while (left is { } value && at < tokens.Count &&
               tokens[at].Kind is Kind.Multiply or Kind.Divide or Kind.Power)
        {
            var op = tokens[at].Kind;
            at++;
            if (Factor(tokens, ref at) is not { } right) return null;
            left = op switch
            {
                Kind.Multiply => value * right,
                Kind.Divide => right == 0 ? double.NaN : value / right,
                _ => Math.Pow(value, right)
            };
        }

        return left;
    }

    private static double? Factor(IReadOnlyList<Token> tokens, ref int at)
    {
        if (at >= tokens.Count) return null;
        var token = tokens[at];

        if (token.Kind == Kind.Subtract)
        {
            at++;
            return Factor(tokens, ref at) is { } negative ? -negative : null;
        }

        if (token.Kind == Kind.Root)
        {
            at++;
            return Factor(tokens, ref at) is { } inside && inside >= 0 ? Math.Sqrt(inside) : null;
        }

        if (token.Kind == Kind.Number)
        {
            at++;
            return token.Value;
        }

        if (token.Kind != Kind.Open) return null;
        at++;
        if (Expression(tokens, ref at) is not { } inner) return null;
        if (at >= tokens.Count || tokens[at].Kind != Kind.Close) return null;
        at++;
        return inner;
    }

    private static string Format(double value)
    {
        var rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);
        var whole = Math.Abs(rounded - Math.Round(rounded)) < 1e-9;
        var text = whole
            ? ((long)Math.Round(rounded)).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.####", CultureInfo.InvariantCulture);
        return text.Replace(".", ",");
    }
}
