using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Jarvis;

internal readonly record struct WindowInfo(nint Handle, uint Pid, string Title);

internal static class Guard
{
    // Сухой прогон: команды разбираются, но реальных действий над системой нет.
    public static bool DryRun { get; set; }
}

internal static class WinCtl
{
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hWnd, nint buf, int max);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hWnd, int index);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hWnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint hWnd, uint msg, nint w, nint l, uint flags, uint ms, out nint result);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hWnd, int attr, out int value, int size);

    private const int GwlExStyle = -20;
    private const int WsExToolwindow = 0x80;
    private const int DwmwaCloaked = 14;
    private const int WmClose = 0x0010;
    private const int WmAppcommand = 0x0319;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const nint HwndBroadcast = 0xffff;
    private const uint SmtoAbortIfHung = 0x2;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;

    private static readonly string[] IgnoredTitles = ["Program Manager"];

    public static List<WindowInfo> TopLevelWindows()
    {
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if ((GetWindowLongW(h, GwlExStyle) & WsExToolwindow) != 0) return true;
            if (IsCloaked(h)) return true;
            var title = TitleOf(h);
            if (title.Length == 0 || IgnoredTitles.Contains(title)) return true;
            GetWindowThreadProcessId(h, out var pid);
            list.Add(new WindowInfo(h, pid, title));
            return true;
        }, nint.Zero);
        return list;
    }

    private static bool IsCloaked(nint hWnd)
    {
        try
        {
            return DwmGetWindowAttribute(hWnd, DwmwaCloaked, out var value, sizeof(int)) == 0 && value != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static string TitleOf(nint hWnd)
    {
        var length = GetWindowTextLengthW(hWnd);
        if (length == 0) return string.Empty;
        var buffer = Marshal.AllocHGlobal((length + 1) * 2);
        try
        {
            GetWindowTextW(hWnd, buffer, length + 1);
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static WindowInfo? Foreground()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == nint.Zero) return null;
        GetWindowThreadProcessId(hWnd, out var pid);
        return new WindowInfo(hWnd, pid, TitleOf(hWnd));
    }

    private static List<WindowInfo> Match(IEnumerable<string> needles)
    {
        var keys = needles.Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 0).ToList();
        var all = TopLevelWindows();
        if (keys.Count == 0) return all;
        return all.Where(w => keys.Any(k => w.Title.ToLowerInvariant().Contains(k))).ToList();
    }

    public static int CloseWindows(IEnumerable<string> needles)
    {
        var mine = Environment.ProcessId;
        var targets = Match(needles).Where(w => w.Pid != mine).ToList();
        if (Guard.DryRun) return targets.Count;
        foreach (var window in targets) SendMessage(window.Handle, WmClose);
        return targets.Count;
    }

    public static int CloseAll()
    {
        var targets = TopLevelWindows().Where(w => w.Pid != Environment.ProcessId).ToList();
        if (Guard.DryRun) return targets.Count;
        foreach (var window in targets) SendMessage(window.Handle, WmClose);
        return targets.Count;
    }

    public static int ToggleWindows(IEnumerable<string> needles, bool minimize)
    {
        var mine = Environment.ProcessId;
        var list = needles.Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        // Свои окна исключаем: иначе «сверни» сворачивает окно самого помощника, и он
        // исчезает с экрана, продолжая слушать.
        var found = list.Count == 0
            ? Foreground() is { } fg ? [fg] : []
            : Match(list);
        var targets = found.Where(w => w.Pid != mine).ToList();
        if (Guard.DryRun) return targets.Count;
        foreach (var window in targets) ShowWindow(window.Handle, minimize ? SwMinimize : SwRestore);
        return targets.Count;
    }

    private static void SendMessage(nint hWnd, uint msg) =>
        SendMessageTimeoutW(hWnd, msg, nint.Zero, nint.Zero, SmtoAbortIfHung, 300, out _);

    public static void BroadcastAppCommand(int appCommand)
    {
        if (Guard.DryRun) return;
        SendMessageTimeoutW(HwndBroadcast, WmAppcommand, nint.Zero, appCommand << 16, SmtoAbortIfHung, 300, out _);
    }

    /// <summary>Клавиша активному окну через PostMessage: физический ввод не трогается,
    /// поэтому помощник разворачивает видео, даже если пользователь держит мышь.</summary>
    public static void PressToForeground(ushort virtualKey, ushort scan)
    {
        if (Guard.DryRun) return;
        var hWnd = GetForegroundWindow();
        if (hWnd == nint.Zero) return;
        var down = (uint)(1 | scan << 16);
        PostMessageW(hWnd, WmKeydown, virtualKey, (nint)(long)down);
        PostMessageW(hWnd, WmKeyup, virtualKey, (nint)(long)(down | 0xc000_0000));
    }

    public static bool Launch(string command)
    {
        if (Guard.DryRun) return true;
        try
        {
            Process.Start(new ProcessStartInfo(command) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Search(string query)
    {
        if (Guard.DryRun) return;
        var url = "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static void OpenUrl(string url)
    {
        if (Guard.DryRun) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

internal static class Volume
{
    private static AudioEndpointVolume Endpoint()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioEndpointVolume;
    }

    public static float Get() => Endpoint().MasterVolumeLevelScalar;

    public static void Set(float scalar)
    {
        if (Guard.DryRun) return;
        Endpoint().MasterVolumeLevelScalar = Math.Clamp(scalar, 0f, 1f);
    }

    public static void Step(float delta) => Set(Get() + delta);

    public static void Mute(bool on)
    {
        if (Guard.DryRun) return;
        Endpoint().Mute = on;
    }

    public static bool IsMuted => Endpoint().Mute;
}
