using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace WordBase.App.Services;

public sealed record PromptAction(string Action, long? EntryId, string Raw)
{
    public static PromptAction FromArguments(IDictionary<string, string>? arguments)
    {
        var args = arguments ?? new Dictionary<string, string>();
        var action = args.TryGetValue("action", out var a) ? a : string.Empty;
        long? entryId = args.TryGetValue("entry", out var e) && long.TryParse(e, out var parsed) ? parsed : null;
        var raw = string.Join("&", args.Select(kv => $"{kv.Key}={kv.Value}"));
        return new PromptAction(action, entryId, raw);
    }

    public static PromptAction Simple(string action, long? entryId) =>
        new(action, entryId, $"action={action}&entry={entryId}");
}

public sealed record NotificationDiagnostics(
    bool Registered,
    string Setting,
    bool QuietTime,
    bool UserAcceptsNotifications,
    bool NativeAvailable,
    string Detail);

public sealed record SelfTestResult(bool Ok, string Summary, NotificationDiagnostics Diagnostics);

/// <summary>
/// 原生 Windows 通知。核心是三层可感知：
/// 1) 发送前查 AppNotificationSetting / 专注助手状态，确定被系统关掉时直接不伪装成功；
/// 2) 发送后靠按钮点击回调确认「真的被看到」；
/// 3) 自检向导让用户随时验证，并能一键切到自绘卡片。
/// </summary>
public sealed partial class NotificationService : IDisposable
{
    private readonly AppLogger _logger;
    private readonly string _iconPath;
    private TaskCompletionSource<PromptAction>? _selfTest;
    private bool _initialized;

    public NotificationService(AppLogger logger, string iconPath)
    {
        _logger = logger;
        _iconPath = iconPath;
    }

    public bool Registered { get; private set; }
    public string? RegisterError { get; private set; }

    public event Action<PromptAction>? Invoked;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            Registered = true;
            _logger.Info("原生通知已注册");
        }
        catch (Exception ex)
        {
            Registered = false;
            RegisterError = ex.Message;
            _logger.Error("注册原生通知失败（将退回自绘卡片）", ex);
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var action = PromptAction.FromArguments(args.Arguments);
        _logger.Info($"通知被点击：{action.Raw}");

        if (action.Action == "selftest")
        {
            _selfTest?.TrySetResult(action);
            return;
        }

        Invoked?.Invoke(action);
    }

    private static AppNotificationSetting CurrentSetting => AppNotificationManager.Default.Setting;

    public NotificationDiagnostics Diagnose()
    {
        var state = QueryNotificationState();
        var setting = Registered ? CurrentSetting : AppNotificationSetting.Unsupported;
        var accepts = state is UserNotificationState.AcceptsNotifications;
        var quiet = state is UserNotificationState.QuietTime;

        var detail = new List<string>
        {
            Registered ? "AUMID 注册：成功" : $"AUMID 注册：失败（{RegisterError ?? "未初始化"}）",
            $"系统通知开关：{Describe(setting)}",
            $"当前通知状态：{Describe(state)}",
            $"程序路径：{Environment.ProcessPath}",
        };

        if (setting is AppNotificationSetting.DisabledForUser or AppNotificationSetting.DisabledForApplication
            or AppNotificationSetting.DisabledByGroupPolicy)
        {
            detail.Add("提示：系统层面已关闭通知，原生通知不会出现。请到「设置 → 系统 → 通知」里允许 WordBase。");
        }

        if (quiet)
        {
            detail.Add("提示：专注助手/勿扰模式正在生效，Toast 会被静默丢弃。");
        }

        return new NotificationDiagnostics(
            Registered,
            Describe(setting),
            quiet,
            accepts,
            Registered && setting is AppNotificationSetting.Enabled && !quiet,
            string.Join(Environment.NewLine, detail));
    }

    /// <summary>发送「该复习了」。返回 false 表示原生通道不可用，调用方应改用自绘卡片。</summary>
    public bool TryShowReviewPrompt(long entryId, string term, string? reading)
    {
        if (!Registered || CurrentSetting is not AppNotificationSetting.Enabled)
        {
            return false;
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddText("WordBase · 该复习了")
                .AddText(term)
                .AddButton(new AppNotificationButton("认识")
                    .AddArgument("action", "known")
                    .AddArgument("entry", entryId.ToString()))
                .AddButton(new AppNotificationButton("不认识")
                    .AddArgument("action", "unknown")
                    .AddArgument("entry", entryId.ToString()));

            if (!string.IsNullOrWhiteSpace(reading))
            {
                builder = builder.AddText(reading);
            }

            var notification = builder.BuildNotification();
            notification.Tag = "review";
            notification.Group = "review";
            AppNotificationManager.Default.Show(notification);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("发送通知失败", ex);
            return false;
        }
    }

    public async Task<SelfTestResult> RunSelfTestAsync(TimeSpan timeout)
    {
        var diagnostics = Diagnose();
        if (!Registered || CurrentSetting is not AppNotificationSetting.Enabled)
        {
            return new SelfTestResult(false,
                "原生通知不可用，建议把通知通道改为「自绘卡片」，或到系统设置里允许 WordBase 通知。", diagnostics);
        }

        _selfTest = new TaskCompletionSource<PromptAction>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("WordBase 通知自检")
                .AddText("请在 15 秒内点一下下面的按钮，以确认通知真的能被看到。")
                .AddButton(new AppNotificationButton("我看到了").AddArgument("action", "selftest"))
                .BuildNotification();
            notification.Tag = "selftest";
            AppNotificationManager.Default.Show(notification);

            var completed = await Task.WhenAny(_selfTest.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed == _selfTest.Task)
            {
                return new SelfTestResult(true, "通知可达：已收到你的点击确认。", diagnostics);
            }

            return new SelfTestResult(false,
                "没有收到点击确认。可能被专注助手吞掉、通知被系统关闭，或者你暂时离开了。可改用「自绘卡片」通道复测。",
                diagnostics);
        }
        catch (Exception ex)
        {
            return new SelfTestResult(false, $"自检失败：{ex.Message}", diagnostics);
        }
        finally
        {
            _selfTest = null;
        }
    }

    private static string Describe(AppNotificationSetting setting) => setting switch
    {
        AppNotificationSetting.Enabled => "已开启",
        AppNotificationSetting.DisabledForApplication => "本应用被关闭",
        AppNotificationSetting.DisabledForUser => "用户范围被关闭",
        AppNotificationSetting.DisabledByGroupPolicy => "被组策略关闭",
        _ => "不支持",
    };

    private static string Describe(UserNotificationState state) => state switch
    {
        UserNotificationState.NotPresent => "用户不在（锁屏或未登录）",
        UserNotificationState.Busy => "忙碌/全屏应用中",
        UserNotificationState.RunningD3dFullScreen => "全屏 3D 程序运行中",
        UserNotificationState.PresentationMode => "演示模式",
        UserNotificationState.AcceptsNotifications => "正常，可接收通知",
        UserNotificationState.QuietTime => "专注助手/勿扰中",
        _ => "未知",
    };

    private static UserNotificationState QueryNotificationState()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0
                ? (UserNotificationState)state
                : UserNotificationState.Unknown;
        }
        catch (Exception)
        {
            return UserNotificationState.Unknown;
        }
    }

    public void Dispose()
    {
        if (!Registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            _logger.Warn($"注销通知失败：{ex.Message}");
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    internal enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        Unknown = 0,
    }
}
