using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WordBase.Core.Models;
using Windows.Graphics;

namespace WordBase.App.Views;

/// <summary>
/// 原生通知被系统吞掉时的兜底提醒：无标题栏的浮动卡片，位于工作区右下角、不抢焦点、25 秒自动收起。
/// 视觉上与主窗口一致（同一套 Fluent 画笔与系统圆角），并作为进入完整复习的入口。
/// </summary>
public sealed partial class PromptWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int AutoCloseSeconds = 25;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    /// <summary>卡片宽度（逻辑像素）。高度按内容自适应。</summary>
    private const double CardWidth = 420;

    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _timer;
    private bool _closed;

    public PromptWindow(Entry entry)
    {
        InitializeComponent();
        TermText.Text = entry.Term;
        ReadingText.Text = entry.ReadingsDisplay;

        var readingMissing = string.IsNullOrWhiteSpace(entry.ReadingsDisplay);
        ReadingText.Visibility = readingMissing ? Visibility.Collapsed : Visibility.Visible;

        MakeBorderless();
        ApplyRoundedCorners();

        // 先给个粗略尺寸，等布局完成再按内容收口，避免高 DPI 下内容被压扁
        AppWindow.Resize(ToPhysical(CardWidth, readingMissing ? 190 : 230));
        PositionAtCorner();

        ((FrameworkElement)Content).Loaded += (_, _) =>
        {
            FitToContent();
            ApplyTopmost();
        };
        ((FrameworkElement)Content).SizeChanged += (_, _) => PositionAtCorner();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AutoCloseSeconds) };
        _timer.Tick += (_, _) => Complete(null);
        _timer.Start();
        Closed += OnClosed;
    }

    public Task<string?> Result => _result.Task;

    public void ShowWithoutActivation()
    {
        try
        {
            AppWindow.Show(activateWindow: false);
        }
        catch (Exception)
        {
            Activate();
        }

        ApplyTopmost();
    }

    /// <summary>
    /// 只靠 OverlappedPresenter.IsAlwaysOnTop 不够稳：Show 之后、或 Resize/Move 之后
    /// 可能被系统重置 z-order。这里再补一次 Win32 置顶，且不抢焦点。
    /// </summary>
    private void ApplyTopmost()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch (Exception)
        {
            // 失败也还有托管层的 IsAlwaysOnTop 兜底
        }
    }

    private void MakeBorderless()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }

    /// <summary>无边框窗口在 Win11 上仍能拿到系统圆角，观感与主窗口的卡片一致。</summary>
    private void ApplyRoundedCorners()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var preference = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // 旧系统不支持就保持直角，不影响功能
        }
    }

    private void PositionAtCorner()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var size = AppWindow.Size;
            AppWindow.Move(new PointInt32(
                area.X + Math.Max(0, area.Width - size.Width - 24),
                area.Y + Math.Max(0, area.Height - size.Height - 24)));
        }
        catch (Exception)
        {
            // 定位失败就用系统默认位置
        }
    }

    /// <summary>按内容真实高度收口，避免固定高度把按钮裁掉。</summary>
    private void FitToContent()
    {
        try
        {
            var root = (FrameworkElement)Content;
            root.Measure(new Windows.Foundation.Size(CardWidth, double.PositiveInfinity));
            var height = root.DesiredSize.Height;
            if (height <= 0 || double.IsNaN(height))
            {
                return;
            }

            AppWindow.Resize(ToPhysical(CardWidth, height));
            PositionAtCorner();
        }
        catch (Exception)
        {
            // 量不出来就沿用粗略尺寸
        }
    }

    /// <summary>AppWindow 用物理像素，布局用逻辑像素，必须换算，否则高 DPI 下内容会被压扁。</summary>
    private SizeInt32 ToPhysical(double width, double height)
    {
        var scale = 1.0;
        try
        {
            var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (dpi > 0)
            {
                scale = dpi / 96.0;
            }
        }
        catch (Exception)
        {
            // 取不到 DPI 就按 100% 处理
        }

        return new SizeInt32(
            (int)Math.Ceiling(width * scale),
            (int)Math.Ceiling(height * scale));
    }

    private void OnKnownClick(object sender, RoutedEventArgs e) => Complete("known");

    private void OnUnknownClick(object sender, RoutedEventArgs e) => Complete("unknown");

    private void OnOpenClick(object sender, RoutedEventArgs e) => Complete("open");

    private void Complete(string? action)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _timer.Stop();
        _result.TrySetResult(action);
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _timer.Stop();
        _result.TrySetResult(null);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
