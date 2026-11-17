namespace Jarvis;

/// <summary>Плеер и видео. Управление идёт через WM_APPCOMMAND — их слышат Spotify, VLC,
/// Яндекс.Музыка и браузеры, а клавиши помощник отправляет только активному окну и только
/// PostMessage: мышь и клавиатура пользователя при этом свободны.</summary>
internal static class Media
{
    public const int Mute = 8;
    public const int VolumeDown = 9;
    public const int VolumeUp = 10;
    public const int Next = 11;
    public const int Previous = 12;
    public const int Stop = 13;
    public const int PlayPause = 14;

    private const ushort VkF = 0x46;
    private const ushort VkEscape = 0x1b;
    private const ushort VkRight = 0x27;
    private const ushort VkLeft = 0x25;
    private const ushort ScanF = 0x20;
    private const ushort ScanEscape = 0x01;
    private const ushort ScanArrow = 0x4d;

    public static void Command(int appCommand) => WinCtl.BroadcastAppCommand(appCommand);

    /// <summary>«F» разворачивает видео и в VLC, и в веб-плеерах, Escape возвращает обратно.</summary>
    public static void Fullscreen(bool on) =>
        WinCtl.PressToForeground(on ? VkF : VkEscape, on ? ScanF : ScanEscape);

    public static void Seek(bool forward) =>
        WinCtl.PressToForeground(forward ? VkRight : VkLeft, ScanArrow);
}
