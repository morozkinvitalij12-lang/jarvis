using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Imaging = System.Drawing.Imaging;

namespace Jarvis;

/// <summary>Кадр экрана с вписанным переводом. Окно открывается с потока ассистента, поэтому
/// всё делается через диспетчер панели; без панели (консольный режим) перевод остаётся озвучить.</summary>
internal static class ScreenOverlay
{
    private const string Title = "JARVIS — перевод экрана";

    private static Window? _window;
    private static DispatcherTimer? _hide;

    /// <summary>Назначает панель при старте. Пока она не задана, показывать некуда.</summary>
    public static Dispatcher? Dispatcher { get; set; }

    /// <summary>False — кадра показать некуда (консольный режим или панель закрывается), и перевод
    /// нужно зачитать. Кадр после вызова принадлежит окну.</summary>
    public static bool Show(Drawing.Bitmap photo)
    {
        using (photo)
        {
            var dispatcher = Dispatcher;
            if (dispatcher is null) return false;
            var source = ToSource(photo);
            try
            {
                dispatcher.Invoke(() => ShowOnUi(source));
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Вызывается до нового захвата: старый перевод не должен попасть в новый кадр.</summary>
    public static void Close()
    {
        var dispatcher = Dispatcher;
        if (dispatcher is null) return;
        try
        {
            dispatcher.Invoke(CloseOnUi);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void ShowOnUi(BitmapSource source)
    {
        CloseOnUi();

        // Размеры рабочего стола и кадра заданы в DIP при 96 dpi, поэтому масштаб считается прямо по ним.
        var work = SystemParameters.WorkArea;
        var scale = Math.Clamp(Math.Min(work.Width * 0.94 / source.Width, work.Height * 0.94 / source.Height), 0.2, 2.0);

        var window = new Window
        {
            Title = Title,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = true,
            Topmost = true,
            // Помощник не перехватывает клавиатуру: окно появляется поверх того, чем заняты.
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Hud.Brush(Hud.Back),
            Width = source.Width * scale,
            Height = source.Height * scale,
        };

        var layout = new Grid();
        layout.Children.Add(new Image { Source = source, Stretch = Stretch.Uniform });
        var hint = Hud.Text("Esc или клик — закрыть", Hud.Dim, 12);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.VerticalAlignment = VerticalAlignment.Bottom;
        hint.Margin = new Thickness(0, 0, 0, 6);
        layout.Children.Add(hint);
        window.Content = layout;

        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseOnUi();
        };
        window.MouseLeftButtonDown += (_, _) => CloseOnUi();

        // Через минуту окно закрывается само: иначе забытый перевод висит поверх работы.
        _hide = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _hide.Tick += (_, _) => CloseOnUi();
        _hide.Start();

        _window = window;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_window, window)) return;
            _window = null;
            _hide?.Stop();
            _hide = null;
        };

        window.Show();
    }

    private static void CloseOnUi()
    {
        _hide?.Stop();
        _hide = null;
        var window = _window;
        _window = null;
        window?.Close();
    }

    private static BitmapSource ToSource(Drawing.Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            Imaging.ImageLockMode.ReadOnly, Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * bitmap.Height, data.Stride);
            // Поток ассистента продолжит работу, а изображение останется в диспетчере панели.
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
