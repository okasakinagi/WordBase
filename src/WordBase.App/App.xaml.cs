using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using WordBase.App.Services;
using WordBase.App.Views;

namespace WordBase.App;

public partial class App : Application
{
    private ReviewSessionWindow? _reviewWindow;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Host?.Logger.Error("未处理异常", e.Exception);
            e.Handled = true;
        };
    }

    public static AppHost Host { get; private set; } = null!;
    public static MainWindow MainWindowInstance { get; private set; } = null!;
    public static AppInstance? MainInstance { get; set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Host = AppHost.Create();
        Host.Notifications.Initialize();
        Host.Notifications.Invoked += prompt => DispatchToUi(() => HandlePromptAction(prompt));

        MainWindowInstance = new MainWindow();
        Host.Tray = new TrayIconService(Host.Logger);
        Host.Tray.Initialize(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        WireTray(Host.Tray);

        Host.AttachUiServices(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), Host.Tray);
        Host.Scheduler!.FallbackPresenter = entry => MainWindowInstance.ShowFallbackPromptAsync(entry);
        Host.Scheduler.Start();
        Host.Scheduler.Pause(Host.Settings.NotificationsPaused);

        try
        {
            Host.Autostart.Reconcile();
        }
        catch (Exception ex)
        {
            Host.Logger.Warn("自启路径校正失败：" + ex.Message);
        }

        if (Host.Settings.NotificationsPaused)
        {
            Host.Tray.SetPaused(true);
        }

        var startupMinimized = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase))
            || Host.Settings.StartMinimized;

        if (TryHandleActivation(ReadInitialActivation()))
        {
            return;
        }

        if (startupMinimized)
        {
            Host.Logger.Info("以最小化模式启动，窗口留在托盘");
        }
        else
        {
            MainWindowInstance.Activate();
        }
    }

    private void WireTray(TrayIconService tray)
    {
        tray.OpenRequested += () => DispatchToUi(() => MainWindowInstance.ShowAndActivate());
        tray.ReviewRequested += () => DispatchToUi(() =>
        {
            MainWindowInstance.ShowAndActivate();
            StartReviewSession();
        });
        tray.SettingsRequested += () => DispatchToUi(() =>
        {
            MainWindowInstance.ShowAndActivate();
            MainWindowInstance.Select("settings");
        });
        tray.PauseToggled += paused => DispatchToUi(() =>
        {
            Host.Settings.NotificationsPaused = paused;
            Host.Scheduler?.Pause(paused);
        });
        tray.ExitRequested += () => DispatchToUi(ExitApp);
    }

    public static void HandleExternalActivation(AppActivationArguments args) =>
        DispatchToUi(() =>
        {
            if (Current is App app)
            {
                app.TryHandleActivation(args);
            }
        });

    private bool TryHandleActivation(AppActivationArguments? args)
    {
        if (args is null)
        {
            return false;
        }

        try
        {
            if (args.Kind == ExtendedActivationKind.AppNotification &&
                args.Data is AppNotificationActivatedEventArgs notificationArgs)
            {
                var prompt = PromptAction.FromArguments(notificationArgs.Arguments);
                Host.Logger.Info($"来自通知的激活：{prompt.Raw}");
                HandlePromptAction(prompt);
                return true;
            }
        }
        catch (Exception ex)
        {
            Host.Logger.Error("处理激活参数失败", ex);
        }

        return false;
    }

    private static AppActivationArguments? ReadInitialActivation()
    {
        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>原生通知按钮 / 自绘卡片按钮的统一入口。</summary>
    public static void HandlePromptAction(PromptAction prompt)
    {
        if (prompt.EntryId is not { } entryId)
        {
            MainWindowInstance.ShowAndActivate();
            return;
        }

        switch (prompt.Action)
        {
            case "known":
                Host.Scheduler?.NoteConfirmed(entryId);
                StartReviewSession(entryId, presetKnown: true);
                break;
            case "unknown":
                Host.Scheduler?.NoteConfirmed(entryId);
                StartReviewSession(entryId, presetKnown: false);
                break;
            case "open":
                Host.Scheduler?.NoteConfirmed(entryId);
                MainWindowInstance.ShowAndActivate();
                break;
            default:
                MainWindowInstance.ShowAndActivate();
                break;
        }
    }

    public static void StartReviewSession(long? entryId = null, bool? presetKnown = null)
    {
        var app = Current as App;
        if (app is null)
        {
            return;
        }

        if (app._reviewWindow is not null)
        {
            try
            {
                app._reviewWindow.Activate();
                return;
            }
            catch (Exception)
            {
                app._reviewWindow = null;
            }
        }

        var window = new ReviewSessionWindow();
        app._reviewWindow = window;
        window.Completed += () =>
        {
            app._reviewWindow = null;
            MainWindowInstance.RefreshCurrent();
        };
        window.Closed += (_, _) => app._reviewWindow = null;

        window.ViewModel.Start(entryId, presetKnown);
        window.Activate();
    }

    private void ExitApp()
    {
        try
        {
            Host.Settings.NotificationsPaused = true;
            Host.Dispose();
        }
        catch (Exception ex)
        {
            Host.Logger.Warn("退出清理失败：" + ex.Message);
        }

        MainWindowInstance.AllowClose();
        MainWindowInstance.Close();
        Exit();
    }

    private static void DispatchToUi(Action action)
    {
        var queue = MainWindowInstance?.DispatcherQueue;
        if (queue is null || queue.TryEnqueue(() => action()))
        {
            return;
        }

        action();
    }
}
