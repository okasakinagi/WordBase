using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WordBase.Core.Models;
using Windows.Graphics;

namespace WordBase.App.Views;

/// <summary>
/// 自绘的角落提醒卡片：不抢焦点、置顶、25 秒自动收起。
/// 原生通知被系统吞掉（专注助手 / 组策略 / 通知关闭）时用它兜底。
/// </summary>
public sealed partial class PromptWindow : Window
{
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _timer;
    private bool _closed;

    public PromptWindow(Entry entry)
    {
        InitializeComponent();
        TermText.Text = entry.Term;
        ReadingText.Text = entry.ReadingsDisplay;

        AppWindow.Resize(new SizeInt32(400, 240));
        PositionAtCorner();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
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
    }

    private void PositionAtCorner()
    {
        try
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            var size = AppWindow.Size;
            AppWindow.Move(new PointInt32(work.Width - size.Width - 24, work.Height - size.Height - 24));
        }
        catch (Exception)
        {
            // 定位失败就用系统默认位置，不影响功能
        }
    }

    private void OnKnownClick(object sender, RoutedEventArgs e) => Complete("known");

    private void OnUnknownClick(object sender, RoutedEventArgs e) => Complete("unknown");

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
        if (!_closed)
        {
            _closed = true;
            _timer.Stop();
            _result.TrySetResult(null);
        }
    }
}
