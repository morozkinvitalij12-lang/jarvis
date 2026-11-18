using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Jarvis;

/// <summary>Окно помощника: вкладка «Пульт» (кольцо состояния, журнал и ввод команд) и вкладка
/// «Настройки». Живёт в трее: F6 выключает и включает слух, Ctrl+Alt+Space показывает окно,
/// закрытие окна прячет помощника, а не убивает его.</summary>
internal sealed class Shell(Config cfg, Assistant? assistant, bool showWindow) : IDisposable
{
    private const int PanelHotkey = 0x4a17;
    private const int PauseHotkey = 0x4a18;
    private const int VkF6 = 0x75;
    private const int ModNoRepeat = 0x4000;

    private readonly Window _window = new() { Title = "Jarvis" };
    private readonly Hud.Ring _ring = new("J.A.R.V.I.S.");
    private readonly TextBox _log = new();
    private readonly TextBox _input = new();
    private readonly TextBox _search = new();
    private readonly StackPanel _rows = new();
    private readonly StackPanel _tabs = new();
    private readonly TextBlock _readout = new();
    private readonly TextBlock _footer = new();
    private readonly Grid _pulse = new();
    private readonly Grid _settings = new();
    private readonly Grid _scenarios = new();
    private readonly StackPanel _scenarioRows = new();
    private readonly Border _scenarioEditor = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox _scenarioName = new();
    private readonly TextBox _scenarioSteps = new();
    private readonly TextBlock _scenarioCaption = new();
    private readonly TextBlock _scenarioHint = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly List<string> _voices = [];

    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _pauseItem;
    private Drawing.Icon? _iconLive;
    private Drawing.Icon? _iconPaused;
    private nint _iconHandleLive;
    private nint _iconHandlePaused;
    private DispatcherFrame? _frame;
    private nint _hotkeyHandle;
    private bool _exiting;
    private bool _pausedShown;
    private int _view;
    private int _selected;
    private int _scenarioSelected;
    private int _editing = -2;
    private List<Settings.Row> _state = [];
    private string _signature = string.Empty;

    public void Run()
    {
        Build();
        // Перевод экрана приходит из потока ассистента, а окно живёт здесь.
        ScreenOverlay.Dispatcher = _window.Dispatcher;
        // В режиме --ui консоль остаётся живой: строки идут и в окно, и в терминал.
        ConsoleHub.Attach(null);
        _window.Dispatcher.UnhandledException += (_, e) =>
        {
            Log.Line($"панель: {e.Exception}");
            e.Handled = true;
        };

        ConsoleHub.OnLine += Append;
        BuildTray();
        RenderTabs();
        RenderRows();
        Refresh();

        _tick.Tick += (_, _) => Refresh();
        _tick.Start();

        // Показываем и тут же прячем, если старт в трей: окно должно создать HWND,
        // иначе глобальные горячие клавиши некому регистрировать.
        _window.Show();
        if (!showWindow) _window.Hide();
        else _window.Activate();

        _frame = new DispatcherFrame();
        Dispatcher.PushFrame(_frame);
        _tick.Stop();
    }

    /// <summary>Вызывается из потока ассистента, когда тот завершился (команда «выход» или сбой).</summary>
    public void OnAssistantStopped()
    {
        try
        {
            if (_window.Dispatcher.HasShutdownStarted || _window.Dispatcher.HasShutdownFinished) return;
            _window.Dispatcher.BeginInvoke(Exit);
        }
        catch (Exception e)
        {
            Log.Line($"остановка панели: {e.Message}");
        }
    }

    /// <summary>Рендерит вёрстку окна в PNG, не показывая его: проверка дизайна без трея,
    /// микрофона и глобальных клавиш.</summary>
    public void Capture(string path, int view)
    {
        Build();
        Select(view);
        _log.AppendText($"JARVIS: конфигурация {cfg.ConfigPath}{Environment.NewLine}");
        _log.AppendText($"JARVIS: голосов доступно {_voices.Count}{Environment.NewLine}");

        const double width = 940;
        const double height = 600;
        _window.Width = width;
        _window.Height = height;

        var content = (FrameworkElement)_window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));

        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);

        var directory = System.IO.Path.GetDirectoryName(path);
        if (directory is not null) Directory.CreateDirectory(directory);
        using var stream = File.Create(path);
        new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } }.Save(stream);
    }

    private void Build()
    {
        _window.WindowStyle = WindowStyle.None;
        _window.AllowsTransparency = true;
        _window.Background = Brushes.Transparent;
        _window.ResizeMode = ResizeMode.NoResize;
        _window.ShowInTaskbar = true;
        _window.Width = 940;
        _window.Height = 600;
        _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _window.FontFamily = new FontFamily(Hud.Face);
        _window.FontSize = 13;
        _window.Foreground = Hud.Brush(Hud.Ink);

        var shell = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Hud.Panel),
            BorderBrush = new SolidColorBrush(Color.FromArgb(180, Hud.CyanDeep.R, Hud.CyanDeep.G, Hud.CyanDeep.B)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 46, ShadowDepth = 0, Opacity = 0.75, Color = Colors.Black }
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        layout.Children.Add(Header());
        var content = new Grid();
        content.Children.Add(_pulse);
        content.Children.Add(_settings);
        content.Children.Add(_scenarios);
        Grid.SetRow(content, 1);
        layout.Children.Add(content);
        var foot = Footer();
        Grid.SetRow(foot, 2);
        layout.Children.Add(foot);

        var stack = new Grid();
        stack.Children.Add(layout);
        stack.Children.Add(Hud.Brackets());

        // Сетка чертежа — под содержимым, но внутри рамки, иначе её перекрывает фон панели.
        var backdrop = new Grid();
        backdrop.Children.Add(new Border
        {
            Background = Hud.Blueprint(),
            CornerRadius = new CornerRadius(6)
        });
        backdrop.Children.Add(stack);
        shell.Child = backdrop;
        _window.Content = shell;

        BuildPulse();
        BuildSettings();
        BuildScenarios();
        Select(0);

        _window.Closing += (_, e) =>
        {
            if (_exiting) return;
            if (assistant is null)
            {
                // Панель без голосовой сессии (--ui) закрывается насовсем.
                _exiting = true;
                if (_frame is { } stopping) stopping.Continue = false;
                return;
            }

            // Закрытие крестиком — только в трей: помощник остаётся слушать.
            e.Cancel = true;
            _window.Hide();
        };
        _window.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || e.OriginalSource is TextBox || Interactive(e.OriginalSource)) return;
            try
            {
                _window.DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        };
        _window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (_scenarioEditor.Visibility == Visibility.Visible)
                {
                    CloseEditor();
                    e.Handled = true;
                    return;
                }

                _window.Hide();
                e.Handled = true;
                return;
            }

            if (_settings.Visibility == Visibility.Visible)
            {
                switch (e.Key)
                {
                    case Key.Down when _state.Count > 0:
                        _selected = Math.Min(_selected + 1, _state.Count - 1);
                        RenderRows();
                        e.Handled = true;
                        break;
                    case Key.Up when _state.Count > 0:
                        _selected = Math.Max(_selected - 1, 0);
                        RenderRows();
                        e.Handled = true;
                        break;
                    case Key.Enter when _state.Count > 0:
                        _state[Math.Clamp(_selected, 0, _state.Count - 1)].Act();
                        RenderRows();
                        e.Handled = true;
                        break;
                }
                return;
            }

            if (_scenarios.Visibility != Visibility.Visible) return;

            // Открытая правка забирает клавиши себе: список не должен переходить и запускать
            // сценарий, пока в полях печатают команды.
            if (_scenarioEditor.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    SaveEditor();
                    e.Handled = true;
                }
                return;
            }

            var count = cfg.Scenarios.Length;
            switch (e.Key)
            {
                case Key.Down when count > 0:
                    _scenarioSelected = Math.Min(_scenarioSelected + 1, count - 1);
                    RenderScenarios();
                    e.Handled = true;
                    break;
                case Key.Up when count > 0:
                    _scenarioSelected = Math.Max(_scenarioSelected - 1, 0);
                    RenderScenarios();
                    e.Handled = true;
                    break;
                case Key.Enter when count > 0:
                    RunScenario(cfg.Scenarios[Math.Clamp(_scenarioSelected, 0, count - 1)]);
                    e.Handled = true;
                    break;
            }
        };

        _window.SourceInitialized += (_, _) =>
        {
            _hotkeyHandle = new WindowInteropHelper(_window).Handle;
            HwndSource.FromHwnd(_hotkeyHandle)?.AddHook(HotkeyHook);
            if (!RegisterHotKey(_hotkeyHandle, PanelHotkey, 0x0003, 0x20))
                Append("[клавиши] Ctrl+Alt+Space занята другим приложением");
            if (!RegisterHotKey(_hotkeyHandle, PauseHotkey, ModNoRepeat, VkF6))
                Append("[клавиши] F6 занята другим приложением");
        };
    }

    private UIElement Header()
    {
        var grid = new Grid { Margin = new Thickness(18, 14, 14, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brand = new TextBlock
        {
            Text = "J.A.R.V.I.S.",
            Foreground = Hud.Brush(Hud.Ink),
            FontSize = 17,
            Margin = new Thickness(0, 0, 26, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Effect = new DropShadowEffect { Color = Hud.Cyan, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.7 }
        };
        Grid.SetColumn(brand, 0);
        grid.Children.Add(brand);

        _tabs.Orientation = Orientation.Horizontal;
        _tabs.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(_tabs, 1);
        grid.Children.Add(_tabs);

        var keys = Hud.Text("F6 — ПАУЗА   ·   CTRL+ALT+SPACE — ОКНО   ·   ESC — СПРЯТАТЬ", Hud.Dim, 11);
        keys.VerticalAlignment = VerticalAlignment.Bottom;
        keys.HorizontalAlignment = HorizontalAlignment.Right;
        keys.Margin = new Thickness(0, 0, 18, 0);
        Grid.SetColumn(keys, 2);
        grid.Children.Add(keys);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        buttons.Children.Add(Glyph("—", () => _window.WindowState = WindowState.Minimized));
        buttons.Children.Add(Glyph("×", Exit));
        Grid.SetColumn(buttons, 3);
        grid.Children.Add(buttons);

        return Wrap(grid);
    }

    private static UIElement Wrap(UIElement content)
    {
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Hud.Hairline),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = content
        };
        return border;
    }

    private UIElement Footer()
    {
        var grid = new Grid { Margin = new Thickness(18, 8, 18, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _footer.Foreground = Hud.Brush(Hud.Dim);
        _footer.FontSize = 11.5;
        Grid.SetColumn(_footer, 0);
        grid.Children.Add(_footer);

        var hint = Hud.Text("трей: правый клик по значку", Hud.CyanDeep, 11);
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(hint, 1);
        grid.Children.Add(hint);

        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Hud.Hairline),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid
        };
        return border;
    }

    private TextBlock Glyph(string mark, Action act)
    {
        var block = new TextBlock
        {
            Text = mark,
            Foreground = Hud.Brush(Hud.Dim),
            FontSize = 16,
            Margin = new Thickness(12, 0, 0, 0),
            Cursor = Cursors.Hand
        };
        block.MouseLeftButtonUp += (_, _) => act();
        block.Tag = "click";
        block.MouseEnter += (_, _) => block.Foreground = Hud.Brush(Hud.Cyan);
        block.MouseLeave += (_, _) => block.Foreground = Hud.Brush(Hud.Dim);
        return block;
    }

    private void BuildPulse()
    {
        _pulse.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pulse.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _pulse.Margin = new Thickness(18, 14, 18, 10);

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        left.Children.Add(_ring.View);
        _readout.Foreground = Hud.Brush(Hud.Dim);
        _readout.FontSize = 11.5;
        _readout.Margin = new Thickness(0, 14, 0, 0);
        _readout.TextAlignment = TextAlignment.Center;
        left.Children.Add(_readout);
        Grid.SetColumn(left, 0);
        _pulse.Children.Add(left);

        var right = new Grid { Margin = new Thickness(22, 0, 0, 0) };
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _log.IsReadOnly = true;
        _log.Background = new SolidColorBrush(Color.FromArgb(120, Hud.Back.R, Hud.Back.G, Hud.Back.B));
        _log.Foreground = Hud.Brush(Hud.Cyan);
        _log.CaretBrush = Hud.Brush(Hud.Cyan);
        _log.BorderBrush = new SolidColorBrush(Hud.Hairline);
        _log.BorderThickness = new Thickness(1);
        _log.FontFamily = new FontFamily(Hud.Mono);
        _log.FontSize = 12.5;
        _log.TextWrapping = TextWrapping.NoWrap;
        _log.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _log.Padding = new Thickness(10, 8, 10, 8);
        Grid.SetRow(_log, 0);
        right.Children.Add(_log);

        var prompt = new Border
        {
            Margin = new Thickness(0, 10, 0, 0),
            BorderBrush = new SolidColorBrush(Hud.Hairline),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Hud.Back),
            Child = promptContent()
        };
        Grid.SetRow(prompt, 1);
        right.Children.Add(prompt);

        Grid.SetColumn(right, 1);
        _pulse.Children.Add(right);
    }

    private UIElement promptContent()
    {
        var dock = new DockPanel { LastChildFill = true };
        var arrow = Hud.Text("»", Hud.Cyan, 14);
        arrow.Margin = new Thickness(10, 0, 8, 0);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(arrow, Dock.Left);
        dock.Children.Add(arrow);

        _input.Background = Brushes.Transparent;
        _input.BorderThickness = new Thickness(0);
        _input.Foreground = Hud.Brush(Hud.Ink);
        _input.CaretBrush = Hud.Brush(Hud.Cyan);
        _input.FontFamily = new FontFamily(Hud.Mono);
        _input.FontSize = 13;
        _input.Padding = new Thickness(0, 8, 8, 8);
        _input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var text = _input.Text.Trim();
            if (text.Length == 0 || assistant is null) return;
            _input.Clear();
            assistant.Submit(text);
            e.Handled = true;
        };
        dock.Children.Add(_input);
        return dock;
    }

    private void BuildSettings()
    {
        _settings.Margin = new Thickness(18, 14, 18, 10);
        _settings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _settings.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _voices.AddRange(PiperTts.InstalledVoices(cfg.PiperVoicesPath));

        _search.Background = Brushes.Transparent;
        _search.Foreground = Hud.Brush(Hud.Ink);
        _search.CaretBrush = Hud.Brush(Hud.Cyan);
        _search.BorderThickness = new Thickness(0);
        _search.FontSize = 13;
        _search.Padding = new Thickness(11, 9, 11, 9);

        var ghost = Hud.Text("поиск строки: микрофон, голос, громкость, проверка…", Hud.Dim, 13);
        ghost.IsHitTestVisible = false;
        ghost.VerticalAlignment = VerticalAlignment.Center;
        ghost.Margin = new Thickness(12, 0, 0, 0);

        var searchGrid = new Grid();
        searchGrid.Children.Add(_search);
        searchGrid.Children.Add(ghost);

        var searchBox = new Border
        {
            Background = new SolidColorBrush(Hud.Back),
            BorderBrush = new SolidColorBrush(Hud.Hairline),
            BorderThickness = new Thickness(1),
            Child = searchGrid
        };
        _search.TextChanged += (_, _) =>
        {
            ghost.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _selected = 0;
            RenderRows();
        };
        Grid.SetRow(searchBox, 0);
        _settings.Children.Add(searchBox);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _rows,
            Margin = new Thickness(0, 10, 0, 0)
        };
        Grid.SetRow(scroll, 1);
        _settings.Children.Add(scroll);

        RenderRows();
    }

    /// <summary>Список сценариев и правка своих: строка запускает сценарий той же командой,
    /// что и голос, а набор сохраняется в jarvis.json — его же читает помощник вслух.</summary>
    private void BuildScenarios()
    {
        _scenarios.Margin = new Thickness(18, 14, 18, 10);
        _scenarios.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _scenarios.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _scenarios.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var note = Hud.Text("сценарий — цепочка обычных команд. запуск щелчком или вслух: «сценарий утро»", Hud.Dim, 11.5);
        note.TextWrapping = TextWrapping.Wrap;
        note.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(note, 0);
        top.Children.Add(note);

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        tools.Children.Add(Link("+ НОВЫЙ", () => OpenEditor(-1)));
        tools.Children.Add(Link("✎ ПРАВИТЬ", () => OpenEditor(_scenarioSelected)));
        tools.Children.Add(Link("✕ УДАЛИТЬ", () => DeleteScenario(_scenarioSelected)));
        Grid.SetColumn(tools, 1);
        top.Children.Add(tools);

        Grid.SetRow(top, 0);
        _scenarios.Children.Add(top);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _scenarioRows,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetRow(scroll, 1);
        _scenarios.Children.Add(scroll);

        Grid.SetRow(ScenarioEditor(), 2);
        _scenarios.Children.Add(_scenarioEditor);

        RenderScenarios();
    }

    private UIElement ScenarioEditor()
    {
        var body = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };

        _scenarioCaption.Foreground = Hud.Brush(Hud.Cyan);
        _scenarioCaption.FontSize = 12;
        _scenarioCaption.Margin = new Thickness(0, 0, 0, 8);
        body.Children.Add(_scenarioCaption);

        Field(_scenarioName, 32);
        _scenarioName.Margin = new Thickness(0, 0, 0, 8);
        body.Children.Add(_scenarioName);

        _scenarioSteps.AcceptsReturn = true;
        _scenarioSteps.TextWrapping = TextWrapping.Wrap;
        _scenarioSteps.MinHeight = 74;
        _scenarioSteps.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Field(_scenarioSteps, 74);
        body.Children.Add(_scenarioSteps);

        _scenarioHint.Foreground = Hud.Brush(Hud.Amber);
        _scenarioHint.FontSize = 11.5;
        _scenarioHint.Margin = new Thickness(0, 8, 0, 0);
        _scenarioHint.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(_scenarioHint);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        actions.Children.Add(Link("СОХРАНИТЬ", SaveEditor));
        actions.Children.Add(Link("ОТМЕНА", CloseEditor));
        body.Children.Add(actions);

        var keys = Hud.Text("каждая строка — обычная команда, та же, что говорят вслух · Ctrl+Enter сохранить, Esc закрыть", Hud.Dim, 11);
        keys.Margin = new Thickness(0, 10, 0, 0);
        keys.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(keys);

        _scenarioEditor.Margin = new Thickness(0, 12, 0, 0);
        _scenarioEditor.BorderBrush = new SolidColorBrush(Hud.Hairline);
        _scenarioEditor.BorderThickness = new Thickness(1);
        _scenarioEditor.Background = new SolidColorBrush(Color.FromArgb(150, Hud.Back.R, Hud.Back.G, Hud.Back.B));
        _scenarioEditor.Child = body;
        return _scenarioEditor;
    }

    /// <summary>Поле ввода в той же сетке, что журнал и строка команд.</summary>
    private static void Field(TextBox box, double height)
    {
        box.Background = new SolidColorBrush(Color.FromArgb(120, Hud.Back.R, Hud.Back.G, Hud.Back.B));
        box.Foreground = Hud.Brush(Hud.Ink);
        box.CaretBrush = Hud.Brush(Hud.Cyan);
        box.BorderBrush = new SolidColorBrush(Hud.Hairline);
        box.BorderThickness = new Thickness(1);
        box.FontFamily = new FontFamily(Hud.Mono);
        box.FontSize = 12.5;
        box.Height = height;
        box.Padding = new Thickness(9, 7, 9, 7);
    }

    private TextBlock Link(string label, Action act)
    {
        var block = Hud.Text(label, Hud.Cyan, 12);
        block.Margin = new Thickness(0, 0, 16, 0);
        block.Cursor = Cursors.Hand;
        block.Tag = "click";
        block.MouseLeftButtonUp += (_, _) => act();
        block.MouseEnter += (_, _) => block.Foreground = Hud.Brush(Hud.Ink);
        block.MouseLeave += (_, _) => block.Foreground = Hud.Brush(Hud.Cyan);
        return block;
    }

    /// <summary>Индекс -1 — новый сценарий, -2 — правка закрыта.</summary>
    private void OpenEditor(int index)
    {
        _editing = index >= 0 && index < cfg.Scenarios.Length ? index : -1;
        var scenario = _editing < 0 ? null : cfg.Scenarios[_editing];
        _scenarioCaption.Text = scenario is null ? "НОВЫЙ СЦЕНАРИЙ" : $"ПРАВКА: {scenario.Name.ToUpperInvariant()}";
        _scenarioName.Text = scenario?.Name ?? string.Empty;
        _scenarioSteps.Text = string.Join("\n", scenario?.Steps ?? []);
        _scenarioHint.Text = string.Empty;
        _scenarioEditor.Visibility = Visibility.Visible;
        _scenarioName.Focus();
        _scenarioName.Select(_scenarioName.Text.Length, 0);
    }

    private void CloseEditor()
    {
        _editing = -2;
        _scenarioEditor.Visibility = Visibility.Collapsed;
    }

    private void SaveEditor()
    {
        var name = _scenarioName.Text.Trim().ToLowerInvariant();
        var steps = _scenarioSteps.Text.Split('\n').Select(step => step.Trim()).Where(step => step.Length > 0).ToArray();
        if (name.Length == 0)
        {
            _scenarioHint.Text = "нужно название: по нему сценарий зовётся вслух";
            return;
        }

        if (steps.Length == 0)
        {
            _scenarioHint.Text = "нужна хотя бы одна команда";
            return;
        }

        var list = cfg.Scenarios.ToList();
        if (_editing >= 0) list[_editing] = new Scenario { Name = name, Steps = steps };
        else
        {
            var clash = list.FindIndex(s => s.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            if (clash >= 0)
            {
                _scenarioHint.Text = $"«{name}» уже есть — правьте его";
                _scenarioSelected = clash;
                OpenEditor(clash);
                return;
            }

            list.Add(new Scenario { Name = name, Steps = steps });
        }

        cfg.Scenarios = list.ToArray();
        cfg.Save();
        _scenarioSelected = _editing < 0 ? list.Count - 1 : _editing;
        CloseEditor();
        RenderScenarios();
    }

    private void DeleteScenario(int index)
    {
        if (index < 0 || index >= cfg.Scenarios.Length)
        {
            Append("[сценарий] удалять нечего: список пуст");
            return;
        }

        var name = cfg.Scenarios[index].Name;
        var list = cfg.Scenarios.ToList();
        list.RemoveAt(index);
        cfg.Scenarios = list.ToArray();
        cfg.Save();
        if (_editing == index) CloseEditor();
        else if (_editing > index) _editing--;
        _scenarioSelected = Math.Clamp(index, 0, Math.Max(0, cfg.Scenarios.Length - 1));
        RenderScenarios();
        Append($"[сценарий] «{name}» удалён, в jarvis.json он больше не значится");
    }

    private void RenderScenarios()
    {
        _scenarioRows.Children.Clear();
        for (var i = 0; i < cfg.Scenarios.Length; i++)
        {
            var index = i;
            var scenario = cfg.Scenarios[i];
            _scenarioRows.Children.Add(ScenarioRow(scenario, i == _scenarioSelected,
                () => { if (_scenarioSelected != index) { _scenarioSelected = index; RenderScenarios(); } },
                () => RunScenario(scenario)));
        }

        if (cfg.Scenarios.Length == 0)
            _scenarioRows.Children.Add(Hud.Text("сценариев нет — нажмите «+ новый» и напишите команды", Hud.Dim, 12.5));
    }

    private void RunScenario(Scenario scenario)
    {
        if (assistant is null)
        {
            Append($"[сценарий] «{scenario.Name}» запустят только голосовая сессия: скажите «сценарий {scenario.Name}»");
            return;
        }

        _scenarioSelected = Math.Max(0, Array.IndexOf(cfg.Scenarios, scenario));
        RenderScenarios();
        assistant.Submit($"сценарий {scenario.Name}");
    }

    /// <summary>Команда из строк настроек идёт тем же путём, что и набранная в окне.</summary>
    private void Submit(string text)
    {
        if (assistant is null)
        {
            Append($"[команда] «{text}» выполнит запущенная голосовая сессия");
            return;
        }

        assistant.Submit(text);
    }

    private static UIElement ScenarioRow(Scenario scenario, bool selected, Action hover, Action click)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 4),
            Background = selected ? new SolidColorBrush(Hud.RowHover) : Brushes.Transparent,
            BorderBrush = selected ? new SolidColorBrush(Color.FromArgb(150, Hud.Cyan.R, Hud.Cyan.G, Hud.Cyan.B)) : Brushes.Transparent,
            BorderThickness = new Thickness(1, 0, 1, 0),
            Cursor = Cursors.Hand
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tile = new Border
        {
            Width = 28,
            Height = 28,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Hud.Amber),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "С",
                Foreground = new SolidColorBrush(Hud.Amber),
                FontSize = 12,
                FontFamily = new FontFamily(Hud.Face),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(tile, 0);
        grid.Children.Add(tile);

        var texts = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Hud.Text(scenario.Name.ToUpperInvariant(), selected ? Hud.Ink : Hud.Cyan, 13));
        texts.Children.Add(Hud.Text(string.Join("  →  ", scenario.Steps), Hud.Dim, 11.5));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        border.Child = grid;
        border.Tag = "click";
        border.MouseEnter += (_, _) => hover();
        border.MouseLeftButtonUp += (_, _) => click();
        return border;
    }

    /// <summary>Клик по вкладке или строке не должен превращаться в перетаскивание окна:
    /// DragMove забирает мышь себе, и MouseLeftButtonUp до элемента не доходит.</summary>
    private static bool Interactive(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Control || node is FrameworkElement { Tag: "click" }) return true;
        return false;
    }

    private void RenderTabs()
    {
        _tabs.Children.Clear();
        _tabs.Children.Add(Tab("ПУЛЬТ", _pulse, () => Select(0)));
        _tabs.Children.Add(Tab("НАСТРОЙКИ", _settings, () => Select(1)));
        _tabs.Children.Add(Tab("СЦЕНАРИИ", _scenarios, () => Select(2)));
    }

    /// <summary>0 — пульт, 1 — настройки, 2 — сценарии: виден только один слой.</summary>
    private void Select(int view)
    {
        _view = view;
        _pulse.Visibility = view == 0 ? Visibility.Visible : Visibility.Collapsed;
        _settings.Visibility = view == 1 ? Visibility.Visible : Visibility.Collapsed;
        _scenarios.Visibility = view == 2 ? Visibility.Visible : Visibility.Collapsed;
        RenderTabs();
    }

    private UIElement Tab(string label, UIElement target, Action click)
    {
        var active = target.Visibility == Visibility.Visible;
        var block = new TextBlock
        {
            Text = label,
            Foreground = Hud.Brush(active ? Hud.Cyan : Hud.Dim),
            FontSize = 12.5,
            Effect = active ? new DropShadowEffect { Color = Hud.Cyan, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.6 } : null
        };

        var border = new Border
        {
            Margin = new Thickness(0, 0, 22, 0),
            Padding = new Thickness(0, 0, 0, active ? 5 : 0),
            BorderBrush = active ? Hud.Brush(Hud.Cyan) : Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, active ? 2 : 0),
            Cursor = Cursors.Hand,
            Child = block
        };

        border.MouseLeftButtonUp += (_, _) => click();
        border.Tag = "click";
        border.MouseEnter += (_, _) => block.Foreground = Hud.Brush(Hud.Cyan);
        border.MouseLeave += (_, _) => block.Foreground = Hud.Brush(active ? Hud.Cyan : Hud.Dim);
        return border;
    }

    private void RenderRows()
    {
        _state = Settings.Rows(cfg, _search.Text, _voices, TogglePause, Submit);
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _state.Count - 1));
        _rows.Children.Clear();
        for (var i = 0; i < _state.Count; i++)
        {
            var index = i;
            _rows.Children.Add(Settings.RowView(_state[i], i == _selected,
                () => { if (_selected != index) { _selected = index; RenderRows(); } },
                () => { _state[index].Act(); RefreshSignature(); RenderRows(); }));
        }

        if (_state.Count == 0)
            _rows.Children.Add(Hud.Text("ничего не найдено", Hud.Dim, 12.5));
    }

    private void Refresh()
    {
        var paused = assistant?.Paused ?? false;
        var phase = assistant?.Phase ?? "настройки";
        _ring.Status.Text = paused ? "ПАУЗА" : phase.ToUpperInvariant();
        _ring.SetActive(!paused && assistant is null or { Running: true } && phase != "стоп");

        var mic = cfg.Microphone < 0 ? "авто" : $"#{cfg.Microphone}";
        _readout.Text = $"МИКРОФОН {mic}   ·   ГОЛОС {cfg.PiperVoice}   ·   {(cfg.NeuralTts ? "PIPER" : "SAPI")}";
        _footer.Text = $"{DateTime.Now.ToString("dddd, HH:mm", CultureInfo.GetCultureInfo("ru-RU"))}   ·   {assistant?.LastHeard ?? string.Empty}";

        if (paused != _pausedShown)
        {
            _pausedShown = paused;
            UpdateTray();
        }

        var signature = $"{paused}|{cfg.PiperVoice}|{cfg.Microphone}|{cfg.Overlay}|{Safe(() => Volume.IsMuted)}|{Safe(() => (int)(Volume.Get() * 100))}";
        if (signature != _signature)
        {
            _signature = signature;
            if (_settings.Visibility == Visibility.Visible) RenderRows();
        }
    }

    private static bool Safe(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int Safe(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void RefreshSignature() => _signature = string.Empty;

    private void Append(string line)
    {
        void Work()
        {
            if (_log.Text.Length > 80000) _log.Text = _log.Text[^40000..];
            _log.AppendText(line + Environment.NewLine);
            _log.ScrollToEnd();
        }

        try
        {
            if (_window.Dispatcher.CheckAccess()) Work();
            else _window.Dispatcher.BeginInvoke(Work);
        }
        catch (Exception e)
        {
            Log.Line($"журнал панели: {e.Message}");
        }
    }

    private void ToggleWindow()
    {
        if (_window.IsVisible && _window.IsActive)
        {
            _window.Hide();
            return;
        }

        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
        _input.Focus();
    }

    private void TogglePause()
    {
        if (assistant is null) return;
        assistant.Paused = !assistant.Paused;
        Append(assistant.Paused ? "[трей] помощник выключен (F6 — включить)" : "[трей] помощник слушает");
        UpdateTray();
        Refresh();
    }

    private void BuildTray()
    {
        // Меню трея — WinForms: без этих вызовов оно рисуется классической серой темой.
        try
        {
            WinForms.Application.EnableVisualStyles();
            WinForms.Application.SetCompatibleTextRenderingDefault(false);
        }
        catch (InvalidOperationException)
        {
        }

        _iconHandleLive = MakeTrayIcon(false, out _iconLive);
        _iconHandlePaused = MakeTrayIcon(true, out _iconPaused);

        var menu = new WinForms.ContextMenuStrip();
        var open = new WinForms.ToolStripMenuItem("Открыть окно");
        open.Click += (_, _) => _window.Dispatcher.BeginInvoke(ToggleWindow);
        _pauseItem = new WinForms.ToolStripMenuItem("Выключить (F6)");
        _pauseItem.Click += (_, _) => _window.Dispatcher.BeginInvoke(TogglePause);
        var quit = new WinForms.ToolStripMenuItem("Выйти");
        quit.Click += (_, _) => _window.Dispatcher.BeginInvoke(Exit);
        menu.Items.AddRange([open, _pauseItem, new WinForms.ToolStripSeparator(), quit]);

        _tray = new WinForms.NotifyIcon
        {
            Icon = _iconLive,
            Visible = true,
            Text = "Jarvis — слушает",
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => _window.Dispatcher.BeginInvoke(ToggleWindow);
    }

    private void UpdateTray()
    {
        if (_tray is null) return;
        var paused = assistant?.Paused ?? false;
        _tray.Icon = paused ? _iconPaused : _iconLive;
        _tray.Text = paused ? "Jarvis — выключен (F6)" : "Jarvis — слушает";
        if (_pauseItem is not null) _pauseItem.Text = paused ? "Включить (F6)" : "Выключить (F6)";
    }

    /// <summary>Значок трея: та же эмблема, что и у exe. «Выключен» — эмблема приглушена и
    /// обведена янтарным кольцом, так что состояние видно, даже не наводя курсор.</summary>
    private static nint MakeTrayIcon(bool paused, out Drawing.Icon icon)
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.Clear(Drawing.Color.Transparent);

            var emblem = Emblem;
            if (emblem is null)
            {
                // Без ассета остаётся процедурное кольцо: пустой значок трея выглядит сломанным.
                var color = paused ? Hud.Amber : Hud.Cyan;
                using var bare = new Drawing.Pen(Drawing.Color.FromArgb(color.R, color.G, color.B), 2.4f);
                graphics.DrawEllipse(bare, 5, 5, 22, 22);
                using var core = new Drawing.SolidBrush(Drawing.Color.FromArgb(230, color.R, color.G, color.B));
                graphics.FillEllipse(core, 13, 13, 6, 6);
            }
            else
            {
                // Полупрозрачность вместо отдельного «тусклого» ассета: затемнение одним
                // ColorMatrix'ом дешевле, чем второй PNG в ресурсах.
                using var attrs = new Drawing.Imaging.ImageAttributes();
                if (paused)
                {
                    attrs.SetColorMatrix(new Drawing.Imaging.ColorMatrix { Matrix33 = 0.45f });
                    graphics.DrawImage(emblem, new Drawing.Rectangle(4, 4, 24, 24),
                        0, 0, emblem.Width, emblem.Height, Drawing.GraphicsUnit.Pixel, attrs);
                    using var off = new Drawing.Pen(Drawing.Color.FromArgb(Hud.Amber.R, Hud.Amber.G, Hud.Amber.B), 2f);
                    graphics.DrawEllipse(off, 1, 1, 30, 30);
                }
                else
                {
                    graphics.DrawImage(emblem, new Drawing.Rectangle(1, 1, 30, 30),
                        0, 0, emblem.Width, emblem.Height, Drawing.GraphicsUnit.Pixel, attrs);
                }
            }
        }

        var handle = bitmap.GetHicon();
        icon = Drawing.Icon.FromHandle(handle);
        return handle;
    }

    /// <summary>Эмблема из ресурсов сборки. Копия нужна потому, что GDI+ читает PNG из потока
    /// лениво: без второго Bitmap поток пришлось бы держать открытым всё время работы трея.</summary>
    private static Drawing.Bitmap? Emblem { get; } = LoadEmblem();

    private static Drawing.Bitmap? LoadEmblem()
    {
        try
        {
            var asm = typeof(Shell).Assembly;
            var name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("jarvis.png", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;

            using var stream = asm.GetManifestResourceStream(name)!;
            using var loaded = new Drawing.Bitmap(stream);
            return new Drawing.Bitmap(loaded);
        }
        catch (Exception e)
        {
            Log.Line($"не удалось взять иконку из ресурсов: {e.Message}");
            return null;
        }
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0312) return IntPtr.Zero;
        switch ((int)(wParam.ToInt64() & 0xFFFF))
        {
            case PanelHotkey:
                ToggleWindow();
                handled = true;
                break;
            case PauseHotkey:
                TogglePause();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        assistant?.Quit();
        _window.Close();
        if (_frame is { } frame) frame.Continue = false;
    }

    public void Dispose()
    {
        ConsoleHub.OnLine -= Append;
        _tick.Stop();
        if (_hotkeyHandle != nint.Zero)
        {
            UnregisterHotKey(_hotkeyHandle, PanelHotkey);
            UnregisterHotKey(_hotkeyHandle, PauseHotkey);
        }

        _tray?.Dispose();
        _iconLive?.Dispose();
        _iconPaused?.Dispose();
        if (_iconHandleLive != nint.Zero) DestroyIcon(_iconHandleLive);
        if (_iconHandlePaused != nint.Zero) DestroyIcon(_iconHandlePaused);
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
}
