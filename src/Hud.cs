using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Shapes = System.Windows.Shapes;

namespace Jarvis;

/// <summary>Палитра и графика панели: тёмный чертёжный фон, светящиеся циан-кольца,
/// янтарная дуга сканирования — как на референсной схеме J.A.R.V.I.S.</summary>
internal static class Hud
{
    public static readonly Color Back = Color.FromRgb(3, 8, 11);
    public static readonly Color Panel = Color.FromRgb(6, 15, 20);
    public static readonly Color Cyan = Color.FromRgb(129, 214, 238);
    public static readonly Color CyanDeep = Color.FromRgb(38, 92, 112);
    public static readonly Color Hairline = Color.FromRgb(20, 46, 58);
    public static readonly Color Ink = Color.FromRgb(205, 231, 240);
    public static readonly Color Dim = Color.FromRgb(88, 128, 145);
    public static readonly Color Amber = Color.FromRgb(232, 168, 66);
    public static readonly Color RowHover = Color.FromRgb(11, 28, 36);

    public const string Face = "Bahnschrift SemiLight, Bahnschrift, Segoe UI";
    public const string Mono = "Cascadia Mono, Consolas, Segoe UI";

    public static SolidColorBrush Brush(Color color) => new(color);

    public static SolidColorBrush Brush(Color color, byte alpha) => new(Color.FromArgb(alpha, color.R, color.G, color.B));

    public static TextBlock Text(string value, Color color, double size = 13, string face = Face) => new()
    {
        Text = value,
        Foreground = Brush(color),
        FontSize = size,
        FontFamily = new FontFamily(face)
    };

    /// <summary>Uppercase с разрядкой — фирменный шрифт HUD на схеме наборный, с широким трекингом.</summary>
    public static string Spaced(string value) => string.Join(" ", value.ToUpperInvariant().ToCharArray());

    /// <summary>Чертёжная сетка 40×40 на тёмном фоне.</summary>
    public static Brush Blueprint()
    {
        var lines = new GeometryGroup
        {
            FillRule = FillRule.Nonzero,
            Children =
            {
                new LineGeometry(new Point(0, 0), new Point(40, 0)),
                new LineGeometry(new Point(0, 0), new Point(0, 40))
            }
        };

        return new DrawingBrush
        {
            Drawing = new GeometryDrawing
            {
                Pen = new Pen(Brush(Hairline, 150), 1),
                Geometry = lines
            },
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            Viewport = new Rect(0, 0, 40, 40),
            ViewportUnits = BrushMappingMode.Absolute,
            Opacity = 0.45
        };
    }

    /// <summary>Четыре угловые скобки по краю окна.</summary>
    public static UIElement Brackets()
    {
        var canvas = new Canvas { IsHitTestVisible = false };
        var ink = Brush(Cyan, 170);
        const double arm = 22;

        void Corner(Point at, double dx, double dy)
        {
            var figure = new PathFigure { StartPoint = new Point(at.X, at.Y + dy * arm) };
            figure.Segments.Add(new LineSegment(new Point(at.X, at.Y), true));
            figure.Segments.Add(new LineSegment(new Point(at.X + dx * arm, at.Y), true));
            var path = new Shapes.Path
            {
                Data = new PathGeometry { Figures = { figure } },
                Stroke = ink,
                StrokeThickness = 1.4
            };
            Canvas.SetLeft(path, 0);
            Canvas.SetTop(path, 0);
            canvas.Children.Add(path);
        }

        // Скобки привязаны к углам через AttachedProperties-подобный пересчёт при размере окна.
        canvas.SizeChanged += (_, _) =>
        {
            canvas.Children.Clear();
            var w = canvas.ActualWidth;
            var h = canvas.ActualHeight;
            Corner(new Point(6, 6), 1, 1);
            Corner(new Point(w - 6, 6), -1, 1);
            Corner(new Point(6, h - 6), 1, -1);
            Corner(new Point(w - 6, h - 6), -1, -1);
        };

        return canvas;
    }

    private static Point OnCircle(double cx, double cy, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(cx + radius * Math.Cos(radians), cy + radius * Math.Sin(radians));
    }

    private static Shapes.Path Arc(double cx, double cy, double radius, double from, double sweep, Color color, double thickness)
    {
        var figure = new PathFigure { StartPoint = OnCircle(cx, cy, radius, from), IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = OnCircle(cx, cy, radius, from + sweep),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = sweep > 180
        });

        return new Shapes.Path
        {
            Data = new PathGeometry { Figures = { figure } },
            Stroke = Brush(color),
            StrokeThickness = thickness,
            Effect = new DropShadowEffect { Color = color, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.8 }
        };
    }

    /// <summary>Кольцо-индикатор состояния: шкала, вращающиеся дуги, имя системы и статус в центре.</summary>
    internal sealed class Ring
    {
        private const double Size = 320;
        private const double Center = Size / 2;

        private readonly RotateTransform _amber = new();
        private readonly RotateTransform _cyan = new();
        private bool _active;

        public Ring(string wordmark)
        {
            var canvas = new Canvas { Width = Size, Height = Size };

            Add(canvas, new Ellipse
            {
                Width = 316,
                Height = 316,
                Stroke = Brush(CyanDeep),
                StrokeThickness = 1
            }, 2, 2);

            Add(canvas, new Ellipse
            {
                Width = 292,
                Height = 292,
                Stroke = Brush(Cyan, 120),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection([1, 4])
            }, 14, 14);

            for (var i = 0; i < 72; i++)
            {
                var major = i % 6 == 0;
                var angle = i * 5.0;
                var inner = OnCircle(Center, Center, major ? 124 : 132, angle);
                var outer = OnCircle(Center, Center, 138, angle);
                Add(canvas, new Line
                {
                    X1 = inner.X,
                    Y1 = inner.Y,
                    X2 = outer.X,
                    Y2 = outer.Y,
                    Stroke = Brush(Cyan, major ? (byte)220 : (byte)90),
                    StrokeThickness = major ? 1.6 : 1
                }, 0, 0);
            }

            Add(canvas, new Ellipse
            {
                Width = 208,
                Height = 208,
                Fill = Brush(Panel, 235),
                Stroke = Brush(Cyan, 200),
                StrokeThickness = 1.2,
                Effect = new DropShadowEffect { Color = Cyan, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.45 }
            }, 56, 56);

            var amber = Arc(Center, Center, 150, 210, 96, Amber, 3);
            amber.RenderTransform = _amber;
            _amber.CenterX = Center;
            _amber.CenterY = Center;
            canvas.Children.Add(amber);

            var cyan = Arc(Center, Center, 108, 20, 150, Cyan, 1.6);
            cyan.RenderTransform = _cyan;
            _cyan.CenterX = Center;
            _cyan.CenterY = Center;
            canvas.Children.Add(cyan);

            var mark = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            mark.Children.Add(new TextBlock
            {
                Text = wordmark,
                Foreground = Brush(Ink),
                FontFamily = new FontFamily(Face),
                FontSize = 27,
                FontWeight = FontWeights.Light,
                HorizontalAlignment = HorizontalAlignment.Center,
                Effect = new DropShadowEffect { Color = Cyan, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.9 }
            });
            Status = new TextBlock
            {
                Text = "ИНИЦИАЛИЗАЦИЯ",
                Foreground = Brush(Cyan),
                FontFamily = new FontFamily(Face),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0)
            };
            mark.Children.Add(Status);

            // Canvas позиционирует детей по левому верху, поэтому подпись центрируется сеткой поверх.
            var grid = new Grid { Width = Size, Height = Size };
            grid.Children.Add(canvas);
            grid.Children.Add(mark);
            View = grid;
        }

        public FrameworkElement View { get; }

        public TextBlock Status { get; }

        public void SetActive(bool active)
        {
            if (_active == active) return;
            _active = active;

            if (active)
            {
                _amber.BeginAnimation(RotateTransform.AngleProperty, Spin(0, 360, 9));
                _cyan.BeginAnimation(RotateTransform.AngleProperty, Spin(360, 0, 14));
            }
            else
            {
                _amber.BeginAnimation(RotateTransform.AngleProperty, null);
                _cyan.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }

        private static DoubleAnimation Spin(double from, double to, double seconds) => new(from, to, TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
    }

    private static void Add(Canvas canvas, UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        canvas.Children.Add(element);
    }
}
