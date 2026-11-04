using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WordBase.App.Views;
using WordBase.Core.Models;
using Windows.Graphics;

namespace WordBase.App;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        SizeToWorkArea();
        AppWindow.Closing += OnClosing;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 620;
        }

        Select("editor");
    }

    /// <summary>按显示器工作区取一个合适的尺寸，避免高 DPI 下窗口过小或超出屏幕。</summary>
    private void SizeToWorkArea()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var width = Math.Min(1400, (int)(area.Width * 0.78));
            var height = Math.Min(960, (int)(area.Height * 0.82));
            AppWindow.Resize(new SizeInt32(Math.Max(width, 900), Math.Max(height, 620)));

            var size = AppWindow.Size;
            AppWindow.Move(new PointInt32(
                area.X + Math.Max(0, (area.Width - size.Width) / 2),
                area.Y + Math.Max(0, (area.Height - size.Height) / 2)));
        }
        catch (Exception)
        {
            AppWindow.Resize(new SizeInt32(1120, 780));
        }
    }

    public string CurrentTag { get; private set; } = "editor";

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        // 关窗 = 收进托盘，后台继续提醒
        args.Cancel = true;
        AppWindow.Hide();
        App.Host.Tray?.ShowBalloon("WordBase 仍在后台运行", "双击托盘图标可以打开，右键可暂停提醒或退出。");
    }

    public void AllowClose() => _allowClose = true;

    public void ShowAndActivate()
    {
        try
        {
            AppWindow.Show();
        }
        catch (Exception)
        {
            // 已经可见时 Show 可能抛异常，忽略后直接激活
        }

        Activate();
    }

    public void Select(string tag)
    {
        CurrentTag = tag;
        ContentFrame.Navigate(TagToPage(tag));

        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                Nav.SelectedItem = item;
                break;
            }
        }
    }

    public void RefreshCurrent() => ContentFrame.Navigate(TagToPage(CurrentTag));

    private static Type TagToPage(string? tag) => tag switch
    {
        "library" => typeof(LibraryPage),
        "review" => typeof(ReviewPage),
        "settings" => typeof(SettingsPage),
        _ => typeof(EntryEditorPage),
    };

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag && tag != CurrentTag)
        {
            CurrentTag = tag;
            ContentFrame.Navigate(TagToPage(tag));
        }
    }

    /// <summary>自绘卡片的兜底通道：展示后按用户选择继续走同一套复习逻辑。</summary>
    public async Task ShowFallbackPromptAsync(Entry entry)
    {
        var window = new PromptWindow(entry);
        window.ShowWithoutActivation();

        var action = await window.Result;
        if (action is null)
        {
            return;
        }

        App.HandlePromptAction(Services.PromptAction.Simple(action, entry.Id));
    }
}
