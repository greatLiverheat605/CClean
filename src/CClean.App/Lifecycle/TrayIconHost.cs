using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CClean.App.Lifecycle;

// Native shell adapter only. The caller owns pause, navigation, cancellation and orderly application exit.
internal sealed class TrayIconHost : IDisposable
{
    internal sealed record TrayActions(Action Open, Action TogglePause, Action Settings, Action Exit, Action<string> Failure);
    private const uint CallbackMessage = 0x8001;
    private const uint IconId = 1;
    private readonly Dispatcher dispatcher;
    private readonly TrayActions actions;
    private readonly HwndSource source;
    private readonly uint taskbarCreated;
    private NotifyIconData icon;
    private bool paused;
    private bool canTogglePause = true;
    private bool exiting;
    private bool disposed;
    private string status = "正在启动，尚未建立观察基线。";

    internal bool IsAvailable { get; private set; }
    internal string? LastError { get; private set; }

    private TrayIconHost(Dispatcher dispatcher, TrayActions actions)
    {
        this.dispatcher = dispatcher;
        this.actions = actions;
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        if (taskbarCreated == 0) throw NativeError("无法登记托盘重建消息。");
        // A hidden top-level tool window receives TaskbarCreated broadcasts; HWND_MESSAGE does not.
        source = new HwndSource(new HwndSourceParameters("CClean tray")
        {
            WindowStyle = 0, ExtendedWindowStyle = 0x80, Width = 0, Height = 0, ParentWindow = IntPtr.Zero
        });
        source.AddHook(WindowMessage);
        var handle = LoadIconW(IntPtr.Zero, new IntPtr(32512)); // shared IDI_APPLICATION, never DestroyIcon
        icon = new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = source.Handle, Id = IconId,
            Flags = 1 | 2 | 4 | 0x80, Callback = CallbackMessage, Icon = handle,
            Tip = Tooltip(status), Info = "", InfoTitle = ""
        };
        try
        {
            if (handle == IntPtr.Zero) throw NativeError("无法加载应用托盘图标。");
            if (!InstallIcon()) throw new InvalidOperationException(LastError);
        }
        catch
        {
            Shell_NotifyIconW(2, ref icon);
            source.RemoveHook(WindowMessage);
            source.Dispose();
            throw;
        }
    }

    internal static bool TryCreate(Dispatcher dispatcher, TrayActions actions, out TrayIconHost? host, out string? error)
    {
        dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(actions);
        host = null;
        try
        {
            host = new(dispatcher, actions);
            error = null;
            return true;
        }
        catch (Exception failure) when (failure is Win32Exception or InvalidOperationException or ArgumentException)
        {
            error = "托盘不可用，窗口必须保持可见并保留明确退出入口：" + failure.Message;
            return false;
        }
    }

    internal bool Update(string status, bool paused, bool canTogglePause = true, bool exiting = false)
    {
        dispatcher.VerifyAccess();
        if (disposed) return false;
        this.status = CleanText(status);
        this.paused = paused;
        this.canTogglePause = canTogglePause;
        this.exiting = exiting;
        icon.Tip = Tooltip(this.status);
        if (!IsAvailable) return false;
        if (Shell_NotifyIconW(1, ref icon)) return true;
        Unavailable("系统托盘状态更新失败；已恢复窗口，请从窗口操作或明确退出。");
        return false;
    }

    private bool InstallIcon()
    {
        if (!Shell_NotifyIconW(0, ref icon))
        {
            LastError = "Shell_NotifyIcon(NIM_ADD) 未成功。";
            IsAvailable = false;
            return false;
        }
        icon.TimeoutOrVersion = 4;
        if (!Shell_NotifyIconW(4, ref icon))
        {
            Shell_NotifyIconW(2, ref icon);
            LastError = "托盘未接受支持键盘交互的版本 4。";
            IsAvailable = false;
            return false;
        }
        IsAvailable = true;
        LastError = null;
        return true;
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (disposed) return IntPtr.Zero;
        if ((uint)message == taskbarCreated)
        {
            IsAvailable = false;
            if (!InstallIcon()) Unavailable("Explorer 重建后托盘恢复失败；窗口已恢复，请从窗口操作或明确退出。");
            handled = true;
        }
        else if ((uint)message == CallbackMessage)
        {
            var notification = (uint)(lParam.ToInt64() & 0xffff);
            var id = (uint)((lParam.ToInt64() >> 16) & 0xffff);
            if (id != IconId) return IntPtr.Zero;
            handled = true;
            // NOTIFYICON_VERSION_4: Enter/Space produces NIN_KEYSELECT; Shift+F10 produces WM_CONTEXTMENU.
            if (notification is 0x400 or 0x401 or 0x203) Run(actions.Open);
            else if (notification == 0x7b) ShowMenu(wParam);
        }
        else if (message == 0x11) // WM_QUERYENDSESSION: this hidden adapter must not veto session ending.
        {
            handled = true;
            return new IntPtr(1);
        }
        return IntPtr.Zero;
    }

    private void ShowMenu(IntPtr packedPosition)
    {
        if (disposed) return;
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) { Unavailable("托盘菜单无法创建，已恢复主窗口。"); return; }
        uint command = 0;
        try
        {
            Add(menu, 1, "打开 CClean(&O)");
            Add(menu, 0, "观察状态：" + MenuText(status), enabled: false);
            Add(menu, 2, paused ? "恢复观察(&P)" : "暂停观察(&P)", canTogglePause && !exiting);
            if (!AppendMenuW(menu, 0x800, UIntPtr.Zero, null)) throw NativeError("无法建立托盘菜单分隔。");
            Add(menu, 3, "设置(&S)", !exiting);
            Add(menu, 4, exiting ? "正在安全退出…" : "退出(&X)", !exiting);
            SetMenuDefaultItem(menu, 1, false);
            var point = MenuPosition(packedPosition);
            SetForegroundWindow(source.Handle);
            command = TrackPopupMenuEx(menu, 0x100 | 0x2, point.X, point.Y, source.Handle, IntPtr.Zero);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { Unavailable("托盘菜单不可用，已恢复主窗口：" + error.Message); }
        finally
        {
            DestroyMenu(menu);
            if (!disposed) PostMessageW(source.Handle, 0, IntPtr.Zero, IntPtr.Zero);
        }
        if (disposed) return;
        switch (command)
        {
            case 1: Run(actions.Open); break;
            case 2 when canTogglePause && !exiting: Run(actions.TogglePause); break;
            case 3 when !exiting: Run(actions.Settings); break;
            case 4 when !exiting: Run(actions.Exit); break;
        }
    }

    private Point MenuPosition(IntPtr packed)
    {
        var point = new Point { X = unchecked((short)(packed.ToInt64() & 0xffff)), Y = unchecked((short)((packed.ToInt64() >> 16) & 0xffff)) };
        if (point.X != -1 || point.Y != -1) return point;
        var identifier = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = source.Handle, Id = IconId };
        if (Shell_NotifyIconGetRect(ref identifier, out var rect) == 0) return new() { X = rect.Left, Y = rect.Bottom };
        if (GetCursorPos(out point)) return point;
        throw NativeError("无法定位托盘菜单。");
    }

    private static void Add(IntPtr menu, uint command, string text, bool enabled = true)
    {
        if (!AppendMenuW(menu, enabled ? 0u : 1u, new UIntPtr(command), text)) throw NativeError("无法建立托盘菜单项。");
    }

    private void Run(Action action)
    {
        if (disposed) return;
        try { action(); }
        catch (Exception error) { actions.Failure("托盘操作未完成：" + error.Message); }
    }

    private void Unavailable(string error)
    {
        IsAvailable = false;
        LastError = error;
        Run(actions.Open); // A hidden process must retain a visible exit path when its shell entry is lost.
        actions.Failure(error);
    }

    private static string CleanText(string? text) => string.IsNullOrWhiteSpace(text) ? "观察状态未知。" :
        text.Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' ');
    private static string MenuText(string text)
    {
        var value = CleanText(text);
        if (value.Length > 96)
        {
            var length = char.IsHighSurrogate(value[94]) ? 94 : 95;
            value = value[..length] + "…";
        }
        return value.Replace("&", "&&");
    }
    private static string Tooltip(string text)
    {
        var value = "CClean · " + CleanText(text);
        if (value.Length <= 127) return value;
        var length = char.IsHighSurrogate(value[126]) ? 126 : 127;
        return value[..length];
    }
    private static Win32Exception NativeError(string message) => new(Marshal.GetLastWin32Error(), message);

    // Pure formatting checks only. Explorer, keyboard navigation, DPI and shell restart require real UI validation.
    internal static IReadOnlyList<string> BoundarySelfCheck()
    {
        var failures = new List<string>();
        var longTip = Tooltip(new string('a', 117) + "😀" + new string('b', 200));
        if (longTip.Length > 127 || char.IsHighSurrogate(longTip[^1])) failures.Add("tooltip-utf16-boundary");
        if (MenuText("a&b\r\nc\0") != "a&&b  c ") failures.Add("menu-text-escaping");
        if (MenuText(new string('a', 300)).Length > 96) failures.Add("menu-status-boundary");
        if (!Tooltip("").StartsWith("CClean · ", StringComparison.Ordinal)) failures.Add("tooltip-name");
        return failures;
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        if (!Shell_NotifyIconW(2, ref icon)) LastError = "Shell 未确认托盘图标移除。";
        IsAvailable = false;
        source.RemoveHook(WindowMessage);
        source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyIconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint operation, ref NotifyIconData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterWindowMessageW(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, bool byPosition);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
