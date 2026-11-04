using System.Runtime.InteropServices;

namespace WordBase.App.Services;

/// <summary>
/// Win32 托盘图标（Shell_NotifyIcon）。WinUI3 没有内建托盘，这里直接调系统 API，
/// 比引入第三方控件更可控，也不受 WinAppSDK 版本影响。
/// </summary>
public sealed partial class TrayIconService : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;

    private const uint MF_STRING = 0;
    private const uint MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x100;
    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const uint LR_DEFAULTSIZE = 0x40;

    private const int CMD_OPEN = 1001;
    private const int CMD_REVIEW = 1002;
    private const int CMD_PAUSE = 1003;
    private const int CMD_SETTINGS = 1004;
    private const int CMD_EXIT = 1005;

    private readonly NativeMethods.WndProcDelegate _wndProc;
    private readonly string _className = "WordBaseTrayWindow";
    private readonly AppLogger _logger;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _added;
    private string _tooltip = "WordBase";
    private bool _paused;

    public TrayIconService(AppLogger logger)
    {
        _logger = logger;
        _wndProc = WindowProc;
    }

    public event Action? OpenRequested;
    public event Action? ReviewRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<bool>? PauseToggled;

    public void Initialize(string? iconPath)
    {
        try
        {
            var hInstance = NativeMethods.GetModuleHandle(null);

            var wc = new NativeMethods.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = _className,
            };
            NativeMethods.RegisterClassEx(ref wc);

            _hwnd = NativeMethods.CreateWindowEx(0, _className, "WordBase", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero,
                hInstance, IntPtr.Zero);

            _hIcon = LoadTrayIcon(iconPath);

            var data = CreateIconData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            _added = NativeMethods.Shell_NotifyIcon(NIM_ADD, ref data);
            if (!_added)
            {
                _logger.Warn("注册托盘图标失败");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("初始化托盘失败", ex);
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        UpdateTooltip(paused ? "WordBase（提醒已暂停）" : "WordBase");
    }

    public void ShowBalloon(string title, string message)
    {
        if (!_added)
        {
            return;
        }

        var data = CreateIconData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = 0x1; // NIIF_INFO
        NativeMethods.Shell_NotifyIcon(1 /* NIM_MODIFY */, ref data);
    }

    private IntPtr LoadTrayIcon(string? iconPath)
    {
        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            var handle = NativeMethods.LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }
        }

        return NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
    }

    private NativeMethods.NOTIFYICONDATA CreateIconData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _hIcon,
        szTip = Truncate(_tooltip, 127),
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void UpdateTooltip(string text)
    {
        _tooltip = text;
        if (!_added)
        {
            return;
        }

        var data = CreateIconData(NIF_TIP | NIF_ICON | NIF_MESSAGE);
        NativeMethods.Shell_NotifyIcon(1 /* NIM_MODIFY */, ref data);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            var mouseMessage = lParam.ToInt64() & 0xFFFF;
            switch (mouseMessage)
            {
                case WM_LBUTTONUP:
                    OpenRequested?.Invoke();
                    return IntPtr.Zero;
                case WM_LBUTTONDBLCLK:
                    OpenRequested?.Invoke();
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                    ShowMenu();
                    return IntPtr.Zero;
            }
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(menu, MF_STRING, CMD_OPEN, "打开 WordBase");
            NativeMethods.AppendMenu(menu, MF_STRING, CMD_REVIEW, "开始复习");
            NativeMethods.AppendMenu(menu, MF_SEPARATOR, 0, string.Empty);
            NativeMethods.AppendMenu(menu, MF_STRING, CMD_PAUSE, _paused ? "恢复提醒" : "暂停提醒");
            NativeMethods.AppendMenu(menu, MF_STRING, CMD_SETTINGS, "设置");
            NativeMethods.AppendMenu(menu, MF_SEPARATOR, 0, string.Empty);
            NativeMethods.AppendMenu(menu, MF_STRING, CMD_EXIT, "退出");
            NativeMethods.SetMenuDefaultItem(menu, CMD_OPEN, 0);

            NativeMethods.GetCursorPos(out var point);
            NativeMethods.SetForegroundWindow(_hwnd);
            var command = NativeMethods.TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, 0,
                _hwnd, IntPtr.Zero);
            NativeMethods.PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CMD_OPEN:
                    OpenRequested?.Invoke();
                    break;
                case CMD_REVIEW:
                    ReviewRequested?.Invoke();
                    break;
                case CMD_PAUSE:
                    _paused = !_paused;
                    UpdateTooltip(_paused ? "WordBase（提醒已暂停）" : "WordBase");
                    PauseToggled?.Invoke(_paused);
                    break;
                case CMD_SETTINGS:
                    SettingsRequested?.Invoke();
                    break;
                case CMD_EXIT:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateIconData(0);
            NativeMethods.Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}

internal static partial class NativeMethods
{
    internal delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    internal static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    internal static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd,
        IntPtr prcRect);

    [DllImport("user32.dll")]
    internal static extern int SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);
}
