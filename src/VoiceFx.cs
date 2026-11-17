namespace Jarvis;

/// <summary>Обработка голоса Piper, чтобы помощник звучал как бортовой ИИ, а не как человек,
/// читающий текст: тон чуть ниже, речь размереннее, появился далёкий комнатный отзвук, а громкость
/// выровнена. Это стилизация обычными фильтрами над свободной моделью голоса — никакие записи
/// актёра не разбираются и не копируются.</summary>
internal sealed class VoiceFx
{
    private readonly double _semitones;
    private readonly double _wet;
    private readonly double _warmth;
    private readonly double _glass;

    private VoiceFx(bool enabled, string name, double semitones, double wet, double warmth, double glass)
    {
        Enabled = enabled;
        Name = name;
        _semitones = semitones;
        _wet = wet;
        _warmth = warmth;
        _glass = glass;
    }

    private static readonly VoiceFx JarvisPreset = new(true, "jarvis", -1.8, 0.08, 4.0, 7.0);
    private static readonly VoiceFx OffPreset = new(false, "off", 0, 0, 0, 0);

    public bool Enabled { get; }
    public string Name { get; }

    public static VoiceFx For(string name) => name.Trim().ToLowerInvariant() switch
    {
        "off" or "выкл" or "нет" or "none" => OffPreset,
        _ => JarvisPreset,
    };

    /// <summary>Нужен вкладке настроек: показать, включена ли обработка, не создавая движок.</summary>
    public static bool IsOn(string name) => For(name).Enabled;

    /// <summary>Множитель тона: меньше единицы — голос ниже. Растяжка, опускающая тон, ровно во
    /// столько же удлиняет фразу, и речь сама становится размеренной. Отдельно подгонять скорость
    /// синтеза нельзя: у Piper паузы между словами сжимаются сильнее, чем сами слова.</summary>
    private double Pitch => Math.Pow(2, _semitones / 12.0);

    public short[] Apply(short[] pcm, int sampleRate)
    {
        if (!Enabled || pcm.Length < sampleRate / 8) return pcm;

        var target = Rms(pcm);
        var work = ToDoubles(Stretch(pcm, Pitch));
        Equalize(work, sampleRate, _warmth, _glass);
        if (_wet > 0) Tail(work, sampleRate, _wet);
        Level(work, target);
        return ToShorts(work);
    }

    /// <summary>Кубической интерполяцией читаем вход с шагом меньше единицы: отсчётов становится
    /// больше, речь растягивается и вместе с ней падает тон. Форманты съезжают следом — из-за
    /// этого голос перестаёт звучать как живой человек, что нам и нужно.</summary>
    private static short[] Stretch(short[] input, double pitch)
    {
        var length = (int)(input.Length / pitch);
        var output = new short[length];
        for (var i = 0; i < length; i++)
        {
            var at = i * pitch;
            var whole = (int)Math.Floor(at);
            output[i] = (short)Math.Clamp(
                CatmullRom(At(input, whole - 1), At(input, whole), At(input, whole + 1), At(input, whole + 2), at - whole),
                -32768.0, 32767.0);
        }

        return output;
    }

    private static double At(short[] input, int at) =>
        at < 0 ? input[0] : at >= input.Length ? input[^1] : input[at];

    private static double CatmullRom(double a, double b, double c, double d, double t) =>
        0.5 * (2 * b + (c - a) * t + (2 * a - 5 * b + 4 * c - d) * t * t + (-a + 3 * b - 3 * c + d) * t * t * t);

    private static double[] ToDoubles(short[] pcm) => Array.ConvertAll(pcm, s => s / 32768.0);

    /// <summary>Плавный ограничитель: делим на тот же множитель, что и внутри, чтобы тихий сигнал
    /// шёл без усиления, а вершины выше 0,8 мягко поджимались.</summary>
    private static short[] ToShorts(double[] work)
    {
        var pcm = new short[work.Length];
        for (var i = 0; i < work.Length; i++)
            pcm[i] = (short)Math.Clamp((int)(Math.Tanh(1.25 * work[i]) / 1.25 * 32767), short.MinValue, short.MaxValue);
        return pcm;
    }

    private static double Rms(short[] pcm)
    {
        double sum = 0;
        foreach (var sample in pcm)
        {
            var value = (double)sample / 32768;
            sum += value * value;
        }

        return Math.Sqrt(sum / Math.Max(1, pcm.Length));
    }

    /// <summary>Громкость фразы после добавок должна остаться той же, иначе ползунок громкости в
    /// настройках перестаёт на что-то влиять.</summary>
    private static void Level(double[] work, double target)
    {
        double sum = 0;
        foreach (var value in work) sum += value * value;
        var current = Math.Sqrt(sum / Math.Max(1, work.Length));
        if (current < 1e-6) return;
        var gain = Math.Clamp(target / current, 0.25, 4.0);
        for (var i = 0; i < work.Length; i++) work[i] *= gain;
    }

    /// <summary>Тембр собран из полочных фильтров на базе фильтра нижних частот: полка — это
    /// «прибавить к сигналу его низкую (или верхнюю) часть, умноженную на нужную громкость». Такая
    /// конструкция не берётся из формул cookbook, где один неверный знак превращает разрез в
    /// подъём, и её отклик проверяется числом на тестовой синусоиде.</summary>
    private static void Equalize(double[] work, int rate, double warmth, double glass)
    {
        Shelf(work, rate, 150, warmth, low: true);
        Shelf(work, rate, 550, -3.0, low: false);
        Shelf(work, rate, 2200, glass, low: false);
        Dull(work, rate, 8000, -8);
        Array.Copy(Highpass(work, rate, 65), work, work.Length);
    }

    private static void Shelf(double[] work, int rate, double hz, double db, bool low)
    {
        var factor = Gain(db) - 1;
        var below = Lowpass(work, rate, hz);
        for (var i = 0; i < work.Length; i++)
            work[i] += factor * (low ? below[i] : work[i] - below[i]);
    }

    private static void Dull(double[] work, int rate, double hz, double db)
    {
        var low = Lowpass(work, rate, hz);
        var keep = Gain(db);
        for (var i = 0; i < work.Length; i++) work[i] = low[i] + (work[i] - low[i]) * keep;
    }

    private static double Gain(double db) => Math.Pow(10, db / 20.0);

    private static double[] Lowpass(double[] input, int rate, double hz)
    {
        var w = 2 * Math.PI * Math.Min(hz, rate / 2.5) / rate;
        var cos = Math.Cos(w);
        var alpha = Math.Sin(w) / Math.Sqrt(2);
        var a0 = 1 + alpha;
        return Filter(input, (1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }

    private static double[] Highpass(double[] input, int rate, double hz)
    {
        var w = 2 * Math.PI * hz / rate;
        var cos = Math.Cos(w);
        var alpha = Math.Sin(w) / Math.Sqrt(2);
        var a0 = 1 + alpha;
        return Filter(input, (1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }

    private static double[] Filter(double[] input, double b0, double b1, double b2, double a1, double a2)
    {
        var output = new double[input.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var x = input[i];
            var y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1;
            x1 = x;
            y2 = y1;
            y1 = y;
            output[i] = y;
        }

        return output;
    }

    /// <summary>Далёкие отражения Шредера: четыре гаснущие гребёнки и два фазовращателя. Задержки
    /// взяты дольше 35 мс именно потому, что короткие гребёнки работают как гребенчатый фильтр:
    /// они подмазывают взрывные согласные, и из-за этого «р» начинает слышиться как «л».</summary>
    private static void Tail(double[] work, int rate, double mix)
    {
        var combs = new[] { 0.0371, 0.0411, 0.0437, 0.0479 };
        var wet = new double[work.Length];
        foreach (var seconds in combs)
        {
            var delay = Math.Max(2, (int)(seconds * rate));
            var buffer = new double[delay];
            var at = 0;
            var damped = 0.0;
            for (var i = 0; i < work.Length; i++)
            {
                var echo = buffer[at];
                damped = echo * 0.4 + damped * 0.6;
                buffer[at] = work[i] + 0.42 * damped;
                wet[i] += echo;
                at = (at + 1) % delay;
            }
        }

        for (var i = 0; i < work.Length; i++) wet[i] /= combs.Length;
        Allpass(wet, rate, 0.005, 0.5);
        Allpass(wet, rate, 0.0017, 0.5);

        var pre = Math.Max(1, (int)(0.018 * rate));
        for (var i = work.Length - 1; i >= 0; i--)
        {
            var source = i - pre;
            work[i] = work[i] * (1 - mix) + (source >= 0 ? wet[source] : 0) * mix;
        }
    }

    private static void Allpass(double[] work, int rate, double seconds, double gain)
    {
        var delay = Math.Max(2, (int)(seconds * rate));
        var buffer = new double[delay];
        var at = 0;
        for (var i = 0; i < work.Length; i++)
        {
            var delayed = buffer[at];
            var result = delayed - gain * work[i];
            buffer[at] = work[i] + gain * result;
            work[i] = result;
            at = (at + 1) % delay;
        }
    }
}
