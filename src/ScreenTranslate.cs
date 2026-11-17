using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinRT;
using WinForms = System.Windows.Forms;

namespace Jarvis;

/// <summary>Перевод того, что написано на экране: захват активного окна, распознавание текста
/// встроенным в Windows OCR (работает офлайн) и перевод построчно. Действие приехало из проекта
/// grussianhotkey, где его запускала горячая клавиша.</summary>
internal static class ScreenTranslate
{
    /// <summary>Photo — кадр с уже вписанным переводом. Text — тот же перевод словами: по нему
    /// помощник зачитает результат, когда панели нет.</summary>
    internal readonly record struct Outcome(Bitmap? Photo, string Text, string Target, int Lines, string? Error);

    private sealed record Line(Rectangle Rect, string Text);

    private const int MaxLines = 30;
    private const int OcrUpscaleLimit = 4000;

    public static Outcome Run()
    {
        try
        {
            return Task.Run(RunAsync).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Log.Line($"экран: {e}");
            return new Outcome(null, string.Empty, "ru", 0, e.Message);
        }
    }

    private static async Task<Outcome> RunAsync()
    {
        ScreenOverlay.Close();

        var bounds = CaptureBounds();
        var scale = Math.Min(1.0, (double)OcrEngine.MaxImageDimension / Math.Max(bounds.Width, bounds.Height));
        var photo = Capture(bounds, scale);
        if (photo is null) return new Outcome(null, string.Empty, "ru", 0, "не удалось захватить экран");

        return await TranslateOn(photo);
    }

    /// <summary>Перевод картинки из файла: тот же путь, что у команды «перевод экрана», но без
    /// захвата экрана — так проверяют шрифт, переводчик и вписку результата в кадр.</summary>
    public static Outcome RunFile(string path)
    {
        try
        {
            return Task.Run(() => TranslateOn(new Bitmap(path))).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Log.Line($"картинка: {e}");
            return new Outcome(null, string.Empty, "ru", 0, e.Message);
        }
    }

    private static async Task<Outcome> TranslateOn(Bitmap photo)
    {
        try
        {
            return await TranslateLines(photo).ConfigureAwait(false);
        }
        catch
        {
            // Кадр создан нами, а вызывающий о нём ещё не знает: иначе GDI+-память
            // висит до финализатора при каждой неудачной попытке.
            photo.Dispose();
            throw;
        }
    }

    private static async Task<Outcome> TranslateLines(Bitmap photo)
    {
        if (OcrEngine.AvailableRecognizerLanguages.Count == 0)
        {
            photo.Dispose();
            return new Outcome(null, string.Empty, "ru", 0, "в Windows нет пакета распознавания текста");
        }

        var lines = await OcrLinesAsync(photo).ConfigureAwait(false);
        if (lines.Count == 0)
        {
            photo.Dispose();
            return new Outcome(null, string.Empty, "ru", 0, "текст не найден");
        }

        var target = TargetFor(lines);
        var taken = Math.Min(lines.Count, MaxLines);
        var done = new string?[taken];
        Parallel.For(0, taken,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, taken) },
            i => done[i] = Translate.Line(lines[i].Text, target));

        var burned = new List<(Rectangle, string)>();
        var spoken = new StringBuilder();
        for (var i = 0; i < taken; i++)
        {
            if (done[i] is not { Length: > 0 } text) continue;
            burned.Add((lines[i].Rect, text));
            if (spoken.Length < 400) spoken.Append(text).Append(". ");
        }

        if (burned.Count == 0)
        {
            photo.Dispose();
            return new Outcome(null, string.Empty, target, 0, "сервис перевода не ответил");
        }

        Burn(photo, burned);
        return new Outcome(photo, spoken.ToString().TrimEnd(), target, burned.Count, null);
    }

    /// <summary>Какие языки распознавания есть в системе — по этой строке видно, нужно ли
    /// доустанавливать пакет OCR в «Языках» Windows.</summary>
    public static string Engines()
    {
        var tags = OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();
        return tags.Length == 0 ? "нет" : string.Join(", ", tags);
    }

    /// <summary>Распознавание по файлу: тот же путь, что у команды «перевод экрана», но без
    /// захвата экрана — проверки пакетов OCR и качества распознавания.</summary>
    public static string[] RecognizeFile(string path)
    {
        using var bitmap = new Bitmap(path);
        return Task.Run(() => OcrLinesAsync(bitmap))
            .GetAwaiter().GetResult()
            .Select(l => l.Text)
            .ToArray();
    }

    /// <summary>Направление выбирается по алфавиту самого экрана: английскую страницу читаем
    /// на русский, русскую — на английский. Отдельной просьбы «на такой-то язык» тут нет.</summary>
    private static string TargetFor(List<Line> lines)
    {
        var russian = lines.Count(l => IsCyrillic(l.Text));
        return russian * 2 > lines.Count ? "en" : "ru";
    }

    private static bool IsCyrillic(string text)
    {
        var letters = 0;
        var cyrillic = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            // Весь кириллический блок, а не строчные а-я: экраны с заголовками КАПСОМ иначе
            // считаются латиницей, русский проход OCR выбрасывается и переводчик получает
            // искажённую английской моделью кашу.
            if (ch is >= 'Ѐ' and <= 'ӿ') cyrillic++;
        }

        return letters > 0 && cyrillic * 2 >= letters;
    }

    private static Rectangle CaptureBounds()
    {
        var screen = WinForms.SystemInformation.VirtualScreen;
        // Свои окна пропускаем: иначе перевод захватывает сам себя, а при запуске со строки
        // настроек — панель помощника вместо нужного окна.
        if (WinCtl.Foreground() is { } window && window.Pid != Environment.ProcessId
            && GetWindowRect(window.Handle, out var r))
        {
            var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            rect.Intersect(screen);
            if (rect.Width > 200 && rect.Height > 200) return rect;
        }

        return screen;
    }

    /// <summary>Один проход StretchBlt с уменьшением: сразу в битмап нужного размера, без
    /// полноэкранной копии в памяти.</summary>
    private static Bitmap? Capture(Rectangle bounds, double scale)
    {
        var width = Math.Max(1, (int)(bounds.Width * scale));
        var height = Math.Max(1, (int)(bounds.Height * scale));
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var drawn = false;
        try
        {
            using var g = Graphics.FromImage(bitmap);
            var hdcDst = g.GetHdc();
            var hdcSrc = GetDC(nint.Zero);
            try
            {
                SetStretchBltMode(hdcDst, Halftone);
                drawn = StretchBlt(hdcDst, 0, 0, width, height, hdcSrc, bounds.X, bounds.Y,
                    bounds.Width, bounds.Height, Srccopy | Captureblt);
            }
            finally
            {
                g.ReleaseHdc(hdcDst);
                ReleaseDC(nint.Zero, hdcSrc);
            }
        }
        finally
        {
            if (!drawn) bitmap.Dispose();
        }

        return drawn ? bitmap : null;
    }

    private static async Task<List<Line>> OcrLinesAsync(Bitmap shot)
    {
        var scale = Math.Max(1.0, Math.Min(1.5, (double)Math.Min(OcrUpscaleLimit, OcrEngine.MaxImageDimension)
                                                / Math.Max(shot.Width, shot.Height)));
        using var enlarged = scale > 1.0 ? Resize(shot, scale) : null;
        var input = enlarged ?? shot;
        using var buffer = ToSoftwareBitmap(input);

        var english = Engine("en");
        var russian = Engine("ru");
        var primary = english ?? russian;
        if (primary is null) return [];

        var lines = Parse(await primary.RecognizeAsync(buffer).AsTask().ConfigureAwait(false));
        if (english is not null && russian is not null)
        {
            // Английская модель читает кириллицу как латиницу, поэтому русские строки берутся
            // вторым проходом, а их прочитанное искажение выбрасывается из первого.
            var again = Parse(await russian.RecognizeAsync(buffer).AsTask().ConfigureAwait(false))
                .Where(l => IsCyrillic(l.Text)).ToList();
            lines.RemoveAll(line => again.Any(other => Overlaps(line.Rect, other.Rect)));
            lines.AddRange(again);
        }

        lines.Sort((a, b) => a.Rect.Top != b.Rect.Top ? a.Rect.Top - b.Rect.Top : a.Rect.Left - b.Rect.Left);

        var back = (double)shot.Width / input.Width;
        return lines.Select(l => l with
        {
            Rect = Rectangle.FromLTRB((int)(l.Rect.Left * back), (int)(l.Rect.Top * back),
                (int)Math.Ceiling(l.Rect.Right * back), (int)Math.Ceiling(l.Rect.Bottom * back))
        }).ToList();
    }

    private static OcrEngine? Engine(string prefix)
    {
        var language = OcrEngine.AvailableRecognizerLanguages
            .FirstOrDefault(l => l.LanguageTag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return language is null ? null : OcrEngine.TryCreateFromLanguage(language);
    }

    private static List<Line> Parse(OcrResult result)
    {
        var lines = new List<Line>();
        foreach (var line in result.Lines)
        {
            var text = line.Text.Trim();
            if (LetterCount(text) < 3) continue;
            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                left = Math.Min(left, r.X);
                top = Math.Min(top, r.Y);
                right = Math.Max(right, r.X + r.Width);
                bottom = Math.Max(bottom, r.Y + r.Height);
            }

            if (right <= left || bottom <= top) continue;
            lines.Add(new Line(new Rectangle((int)left, (int)top, (int)(right - left), (int)(bottom - top)), text));
        }

        return lines;
    }

    private static int LetterCount(string text) => text.Count(char.IsLetter);

    private static bool Overlaps(Rectangle a, Rectangle b)
    {
        var both = Rectangle.Intersect(a, b);
        return both.Height > Math.Min(a.Height, b.Height) * 0.5 && both.Width > Math.Min(a.Width, b.Width) * 0.5;
    }

    private static Bitmap Resize(Bitmap source, double scale)
    {
        var bitmap = new Bitmap((int)(source.Width * scale), (int)(source.Height * scale), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, bitmap.Width, bitmap.Height);
        return bitmap;
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var software = new SoftwareBitmap(BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height, BitmapAlphaMode.Ignore);
            try
            {
                using var buffer = software.LockBuffer(BitmapBufferAccessMode.Write);
                using var reference = buffer.CreateReference();
                // CreateReference даёт IInspectable-обёртку: до нативного интерфейса нужен QueryInterface.
                var access = reference.As<IMemoryBufferByteAccess>();
                access.GetBuffer(out var dst, out var capacity);
                CopyMemory(dst, data.Scan0, Math.Min(data.Stride * data.Height, (int)capacity));
                return software;
            }
            catch
            {
                software.Dispose();
                throw;
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }

    /// <summary>Перевод вписывается прямо в кадр: цвет под строкой берётся из её же среднего цвета,
    /// а кегль подбирается бинарным поиском, чтобы строка влезла в своё место. Пиксели и контекст
    /// рисования используются один раз на весь экран, а не по разу на строку.</summary>
    private static void Burn(Bitmap photo, List<(Rectangle Rect, string Text)> items)
    {
        var back = new Color[items.Count];
        var data = photo.LockBits(new Rectangle(0, 0, photo.Width, photo.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var i = 0; i < items.Count; i++) back[i] = Average(data, photo.Size, items[i].Rect);
        }
        finally
        {
            photo.UnlockBits(data);
        }

        using var g = Graphics.FromImage(photo);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
            LineAlignment = StringAlignment.Center,
        };

        for (var i = 0; i < items.Count; i++)
        {
            var (rect, text) = items[i];
            if (rect.Width <= 4 || rect.Height <= 4) continue;
            var ink = Luminance(back[i]) > 140 ? Color.FromArgb(20, 20, 24) : Color.FromArgb(240, 242, 246);

            double low = 9, high = Math.Max(9, rect.Height * 0.82), best = 9;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                using var probe = new Font("Segoe UI", (float)middle, FontStyle.Regular, GraphicsUnit.Pixel);
                if (g.MeasureString(text, probe, int.MaxValue, format).Width <= rect.Width - 4)
                {
                    best = middle;
                    low = middle + 0.5;
                }
                else high = middle - 0.5;
            }

            using var font = new Font("Segoe UI", (float)best, FontStyle.Regular, GraphicsUnit.Pixel);
            using (var background = new SolidBrush(back[i])) g.FillRectangle(background, rect);
            using (var foreground = new SolidBrush(ink))
                g.DrawString(text, font, foreground, new RectangleF(rect.X + 2, rect.Y, rect.Width - 4, rect.Height), format);
        }
    }

    private static Color Average(BitmapData data, Size size, Rectangle at)
    {
        var left = Math.Clamp(at.X, 0, size.Width - 1);
        var right = Math.Clamp(at.Right, left + 1, size.Width);
        var top = Math.Clamp(at.Y, 0, size.Height - 1);
        var bottom = Math.Clamp(at.Bottom, top + 1, size.Height);

        var row = new byte[(right - left) * 4];
        long red = 0, green = 0, blue = 0, count = 0;
        var stepY = Math.Max(1, (bottom - top) / 8);
        for (var y = top; y < bottom; y += stepY)
        {
            Marshal.Copy(nint.Add(data.Scan0, y * data.Stride + left * 4), row, 0, row.Length);
            for (var i = 0; i + 2 < row.Length; i += 4)
            {
                blue += row[i];
                green += row[i + 1];
                red += row[i + 2];
                count++;
            }
        }

        if (count == 0) return Color.FromArgb(24, 26, 30);
        return Color.FromArgb((int)(red / count), (int)(green / count), (int)(blue / count));
    }

    private static float Luminance(Color color) => 0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    private const int Halftone = 4;
    private const int Srccopy = 0x00CC0020;
    private const int Captureblt = 0x40000000;

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hdc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint hdcDst, int xDst, int yDst, int wDst, int hDst,
        nint hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] private static extern void CopyMemory(nint dst, nint src, int length);
}
