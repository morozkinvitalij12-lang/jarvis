using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Jarvis;

/// <summary>Строки вкладки «Настройки»: то же управление, что было в плавающей панели,
/// только теперь внутри окна помощника.</summary>
internal static class Settings
{
    internal sealed class Row
    {
        public string Tile = "·";
        public Color TileColor = Hud.Cyan;
        public string Title = "";
        public string Subtitle = "";
        public string Hint = "";
        public Action Act = () => { };
    }

    private static int _scenarioAt = -1;

    public static List<Row> Rows(Config cfg, string filter, List<string> voices, Action togglePause, Action<string> run)
    {
        var micIndex = cfg.Microphone < 0 ? Microphones.PickDefault() : cfg.Microphone;
        var muted = Safe(() => Volume.IsMuted, false);
        var volume = Safe(() => (int)(Volume.Get() * 100), 0);

        var all = new List<Row>
        {
            new()
            {
                Tile = "М",
                TileColor = Hud.Cyan,
                Title = "Микрофон",
                Subtitle = "устройство записи · Enter — следующее",
                Hint = cfg.Microphone < 0 ? "авто" : Safe(() => NAudio.Wave.WaveInEvent.GetCapabilities(cfg.Microphone).ProductName, $"#{cfg.Microphone}"),
                Act = () =>
                {
                    cfg.Microphone = cfg.Microphone + 1 >= NAudio.Wave.WaveInEvent.DeviceCount ? -1 : cfg.Microphone + 1;
                    cfg.Save();
                }
            },
            new()
            {
                Tile = "Г",
                TileColor = Hud.Amber,
                Title = "Голос",
                Subtitle = "нейросетевой голос ответов · Enter — следующий",
                Hint = cfg.PiperVoice,
                Act = () =>
                {
                    if (voices.Count == 0) return;
                    var at = voices.FindIndex(v => v.Contains(cfg.PiperVoice, StringComparison.OrdinalIgnoreCase));
                    cfg.PiperVoice = voices[(at + 1) % voices.Count].Replace("ru_RU-", "").Replace("-medium", "");
                    cfg.Save();
                }
            },
            new()
            {
                Tile = "Т",
                TileColor = Hud.CyanDeep,
                Title = "Движок речи",
                Subtitle = "нейросеть или системный SAPI · Enter — переключить",
                Hint = cfg.NeuralTts ? "piper" : "sapi",
                Act = () =>
                {
                    cfg.TtsBackend = cfg.NeuralTts ? "sapi" : "piper";
                    cfg.Save();
                }
            },
            new()
            {
                Tile = "С",
                TileColor = Hud.Cyan,
                Title = "Слух",
                Subtitle = "нейросеть GigaAM или только словарь Vosk · Enter — переключить, нужна перезагрузка",
                Hint = cfg.NeuralAsr ? (Directory.Exists(cfg.NeuralAsrPath) ? "gigaam" : "нет модели") : "vosk",
                Act = () =>
                {
                    cfg.NeuralAsr = !cfg.NeuralAsr;
                    cfg.Save();
                }
            },
            new()
            {
                Tile = "З",
                TileColor = Hud.Amber,
                Title = "Громкость",
                Subtitle = "Enter — +10%, применяется сразу",
                Hint = muted ? "звук отключён" : $"{volume}%",
                Act = () => Volume.Set((volume + 10) % 110 / 100f)
            },
            new()
            {
                Tile = "З",
                TileColor = Hud.Amber,
                Title = muted ? "Включить звук" : "Отключить звук",
                Subtitle = "заглушить вывод системы",
                Act = () => Volume.Mute(!muted)
            },
            new()
            {
                Tile = "F6",
                TileColor = Hud.Cyan,
                Title = "Пауза",
                Subtitle = "помощник замолкает и перестаёт слушать · F6",
                Act = togglePause
            },
            new()
            {
                Tile = "О",
                TileColor = Hud.CyanDeep,
                Title = "Окно при запуске",
                Subtitle = "иначе помощник стартует сразу в трее",
                Hint = cfg.Overlay ? "показывать" : "трей",
                Act = () =>
                {
                    cfg.Overlay = !cfg.Overlay;
                    cfg.Save();
                }
            },
            new()
            {
                Tile = "Н",
                TileColor = Hud.CyanDeep,
                Title = "Умные ответы",
                Subtitle = "локальная нейросеть, когда команда не понята · Enter — вкл/выкл",
                Hint = cfg.SmartAnswers ? cfg.OllamaModel : "выключено",
                Act = () =>
                {
                    cfg.SmartAnswers = !cfg.SmartAnswers;
                    cfg.Save();
                }
            },
            new()
            {
                Tile = "Ц",
                TileColor = Hud.CyanDeep,
                Title = "Сценарии",
                Subtitle = "цепочки команд · Enter — запустить следующий, список правится в jarvis.json",
                Hint = cfg.Scenarios.Length == 0
                    ? "нет"
                    : $"{cfg.Scenarios.Length}: {string.Join(", ", cfg.Scenarios.Select(s => s.Name))}",
                Act = () =>
                {
                    if (cfg.Scenarios.Length == 0) return;
                    _scenarioAt = (_scenarioAt + 1) % cfg.Scenarios.Length;
                    run($"сценарий {cfg.Scenarios[_scenarioAt].Name}");
                }
            },
            new()
            {
                Tile = "Э",
                TileColor = Hud.CyanDeep,
                Title = "Перевод экрана",
                Subtitle = "распознаёт текст активного окна и показывает его перевод",
                Act = () => run("перевод экрана")
            },
            new()
            {
                Tile = "Я",
                TileColor = Hud.CyanDeep,
                Title = "Переводчик экрана",
                Subtitle = "своим распознавателем или кадром в Яндекс Переводчике · Enter — переключить",
                Hint = cfg.YandexScreen ? "Яндекс" : "локальный",
                Act = () =>
                {
                    cfg.ScreenTranslator = cfg.YandexScreen ? "local" : "yandex";
                    cfg.Save();
                }
            },
            new()
            {
                Tile = "П",
                TileColor = Hud.Dim,
                Title = "Проверить микрофон",
                Subtitle = "короткая запись: есть ли сигнал, вывод в журнал",
                Act = () => ThreadPool.QueueUserWorkItem(_ =>
                    Console.WriteLine($"[проверка] шум микрофона #{micIndex}: {Microphones.Measure(micIndex, cfg.SampleRate):F0}"))
            },
            new()
            {
                Tile = "П",
                TileColor = Hud.Dim,
                Title = "Проверить нейросеть",
                Subtitle = "Ollama по адресу из jarvis.json и наличие модели, вывод в журнал",
                Act = () => ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        foreach (var line in Brain.ReportAsync(cfg, string.Empty).GetAwaiter().GetResult().Split('\n'))
                            Console.WriteLine($"[мозг] {line}");
                    }
                    catch (Exception e)
                    {
                        Log.Line($"проверка нейросети: {e}");
                        Console.WriteLine($"[мозг] {e.Message}");
                    }
                })
            },
            new()
            {
                Tile = "П",
                TileColor = Hud.Dim,
                Title = "Самопроверка",
                Subtitle = "модель, голос и цепочка TTS→STT, вывод в журнал",
                Act = () => ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Selftest.Run(cfg);
                    }
                    catch (Exception e)
                    {
                        Log.Line($"самопроверка: {e.Message}");
                        Console.WriteLine($"[проверка] самопроверка: {e.Message}");
                    }
                })
            },
            new()
            {
                Tile = "П",
                TileColor = Hud.Dim,
                Title = "Примеры голосов",
                Subtitle = $"записать WAV каждого голоса в {cfg.DictationDir}",
                Act = () => ThreadPool.QueueUserWorkItem(_ =>
                {
                    foreach (var name in voices)
                    {
                        try
                        {
                            using var tts = new PiperTts(Path.Combine(cfg.PiperVoicesPath, name), cfg.EspeakDataPath, cfg.PiperSpeed, cfg.PiperThreads);
                            Directory.CreateDirectory(cfg.DictationDir);
                            Wav.Write(Path.Combine(cfg.DictationDir, $"голос-{name}.wav"), tts.Render(cfg.Greeting), tts.SampleRate);
                            Console.WriteLine($"[голос] {name} → голос-{name}.wav");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"[голос] {name}: {e.Message}");
                        }
                    }
                })
            }
        };

        var needle = filter.Trim();
        if (needle.Length == 0) return all;
        return all.Where(r => (r.Title + " " + r.Subtitle).Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static UIElement RowView(Row row, bool selected, Action hover, Action click)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 10, 6),
            Background = selected ? new SolidColorBrush(Hud.RowHover) : Brushes.Transparent,
            BorderBrush = selected ? new SolidColorBrush(Color.FromArgb(150, Hud.Cyan.R, Hud.Cyan.G, Hud.Cyan.B)) : Brushes.Transparent,
            BorderThickness = new Thickness(1, 0, 1, 0),
            Cursor = Cursors.Hand
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tile = new Border
        {
            Width = 28,
            Height = 28,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(row.TileColor),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = row.Tile,
                Foreground = new SolidColorBrush(row.TileColor),
                FontSize = 12,
                FontFamily = new FontFamily(Hud.Face),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(tile, 0);
        grid.Children.Add(tile);

        var texts = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Hud.Text(row.Title.ToUpperInvariant(), selected ? Hud.Ink : Hud.Cyan, 13));
        if (row.Subtitle.Length > 0)
            texts.Children.Add(Hud.Text(row.Subtitle, Hud.Dim, 11.5));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var hint = Hud.Text(row.Hint, Hud.Dim, 12);
        hint.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(hint, 2);
        grid.Children.Add(hint);

        border.Child = grid;
        border.Tag = "click";
        border.MouseEnter += (_, _) => hover();
        border.MouseLeftButtonUp += (_, _) => click();
        return border;
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
