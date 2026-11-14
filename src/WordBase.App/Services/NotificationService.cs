using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using WordBase.Core.Models;

namespace WordBase.App.Services;

/// <summary>
/// 通知按钮带回的参数。所有状态都编码在通知自身里（不依赖进程内状态），
/// 这样即使通知被系统重新投递、或 App 重启后再点击，也能正确结算。
/// </summary>
public sealed record PromptAction(
    string Action,
    long? EntryId,
    int? Pick,
    bool IsCorrect,
    long? ConfusedWith,
    Rating? Rating,
    string? BaseInteraction,
    string Raw)
{
    public static PromptAction FromArguments(IDictionary<string, string>? arguments)
    {
        var args = arguments ?? new Dictionary<string, string>();
        var raw = string.Join("&", args.Select(kv => $"{kv.Key}={kv.Value}"));

        long? Long(string key) =>
            args.TryGetValue(key, out var v) &&
            long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

        var action = args.TryGetValue("action", out var a) ? a : string.Empty;
        int? pick = Long("pick") is { } p ? (int)p : null;
        var isCorrect = args.TryGetValue("correct", out var c) && c == "1";
        Rating? rating = args.TryGetValue("rate", out var r) && Enum.TryParse<Rating>(r, true, out var parsedRating)
            ? parsedRating
            : null;

        // other=0 表示没有可记录的混淆对象
        var other = Long("other");
        var baseInteraction = args.TryGetValue("base", out var b) ? b : null;

        return new PromptAction(action, Long("entry"), pick, isCorrect, other is > 0 ? other : null, rating,
            baseInteraction, raw);
    }

    public static PromptAction Simple(string action, long? entryId) =>
        new(action, entryId, null, false, null, null, null, $"action={action}&entry={entryId}");
}

public sealed record NotificationDiagnostics(
    bool Registered,
    string Setting,
    bool QuietTime,
    bool UserAcceptsNotifications,
    bool NativeAvailable,
    string Detail);

public sealed record SelfTestResult(bool Ok, string Summary, NotificationDiagnostics Diagnostics);

/// <summary>一道选择题的一个选项。SourceEntryId 只用于「答错时记混淆对」，正确项为 null。</summary>
public sealed record QuizChoice(string Text, bool IsCorrect, long? SourceEntryId);

/// <summary>
/// 原生 Windows 通知。核心是三层可感知：
/// 1) 发送前查 AppNotificationSetting / 专注助手状态，确定被系统关掉时直接不伪装成功；
/// 2) 发送后靠按钮点击回调确认「真的被看到」；
/// 3) 自检向导让用户随时验证，并能一键切到自绘卡片。
///
/// 复习通知使用 Reminder 场景：不点不会自己消失（普通 toast 约 5 秒就飘走），
/// 由调度器在 5 分钟后主动收起，避免用户「来不及点」只能去通知中心翻。
/// </summary>
public sealed partial class NotificationService : IDisposable
{
    /// <summary>Windows 的 toast 最多 5 个按钮，超过不会被渲染。</summary>
    public const int MaxButtons = 5;

    /// <summary>选项按钮上的释义截断长度。Toast 按钮宽度有限，太长会被系统截得更难看。</summary>
    public const int MaxChoiceLength = 14;

    public const string ReviewTag = "review";
    private const string ReviewGroup = "review";

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

    public bool IsUsable => Registered && CurrentSetting is AppNotificationSetting.Enabled;

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
            IsUsable && !quiet,
            string.Join(Environment.NewLine, detail));
    }

    // ---------------- 复习相关通知 ----------------

    /// <summary>「认识 / 不认识」提问。返回 false 表示原生通道不可用，调用方应改用自绘卡片。</summary>
    public bool TryShowAskKnown(long entryId, string term, string? reading)
    {
        var builder = NewReviewBuilder("该复习了")
            .AddText(term)
            .AddButton(Action("不认识", ("action", "unknown"), ("entry", entryId)))
            .AddButton(Action("认识", ("action", "known"), ("entry", entryId)))
            .AddButton(Action("稍后 10 分钟", ("action", "snooze"), ("entry", entryId)))
            .AddButton(Action("打开 WordBase", ("action", "open"), ("entry", entryId)));

        AppendReading(builder, reading);
        return ShowReview(builder.BuildNotification());
    }

    /// <summary>直接出选择题：4 个选项 + 1 个「无法选择」出口，正好用满系统允许的 5 个按钮。</summary>
    public bool TryShowQuiz(long entryId, string term, string? reading, IReadOnlyList<QuizChoice> choices)
    {
        if (choices.Count == 0)
        {
            return false;
        }

        var builder = NewReviewBuilder("选一个正确的意思").AddText(term);

        for (var i = 0; i < choices.Count && i < MaxButtons - 1; i++)
        {
            var choice = choices[i];
            builder = builder.AddButton(Action(Truncate(choice.Text, MaxChoiceLength),
                ("action", "quiz"),
                ("entry", entryId),
                ("pick", i),
                ("correct", choice.IsCorrect ? 1 : 0),
                ("other", choice.SourceEntryId ?? 0)));
        }

        builder = builder.AddButton(Action("无法选择 · 打开主界面",
            ("action", "open"),
            ("entry", entryId)));

        AppendReading(builder, reading);
        return ShowReview(builder.BuildNotification());
    }

    /// <summary>揭示答案；withRatings 为 true 时给四档自评按钮。</summary>
    public bool TryShowAnswer(long entryId, string term, IEnumerable<string> answerLines, bool withRatings,
        string baseInteraction, long? confusedWith = null)
    {
        var builder = NewReviewBuilder("答案").AddText(term);

        foreach (var line in answerLines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(3))
        {
            builder = builder.AddText(Truncate(line, 90));
        }

        if (withRatings)
        {
            AppNotificationButton Rate(string label, string value) =>
                Action(label, ("action", "rate"), ("entry", entryId), ("rate", value),
                    ("base", baseInteraction), ("other", confusedWith ?? 0));

            builder = builder
                .AddButton(Rate("忘记", "Again"))
                .AddButton(Rate("模糊", "Hard"))
                .AddButton(Rate("记得", "Good"))
                .AddButton(Rate("很轻松", "Easy"));
        }
        else
        {
            builder = builder.AddButton(Action("知道了", ("action", "dismiss"), ("entry", entryId)));
        }

        return ShowReview(builder.BuildNotification());
    }

    /// <summary>结算完成后原地换成「已完成」，由调度器 3 秒后收起。</summary>
    public bool TryShowCompleted()
    {
        var builder = NewReviewBuilder("已完成").AddText("已记录，无需再操作。");
        return ShowReview(builder.BuildNotification());
    }

    /// <summary>一批词到期：汇总成一条，点开在主窗口一次性做完。</summary>
    public bool TryShowSummary(int dueCount)
    {
        var builder = NewReviewBuilder("有词待复习")
            .AddText($"现在有 {dueCount} 个词到期")
            .AddText("点下面开始，在主界面一次做完。")
            .AddButton(Action("开始复习", ("action", "startBulk")))
            .AddButton(Action("稍后 10 分钟", ("action", "snooze")));

        return ShowReview(builder.BuildNotification());
    }

    /// <summary>主动收起复习通知（「已完成」停留结束、或超时未响应时调用）。</summary>
    public async Task RemoveReviewAsync()
    {
        if (!Registered)
        {
            return;
        }

        try
        {
            await AppNotificationManager.Default.RemoveByTagAsync(ReviewTag).AsTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"收起通知失败：{ex.Message}");
        }
    }

    private AppNotificationBuilder NewReviewBuilder(string headline) =>
        new AppNotificationBuilder()
            .SetScenario(AppNotificationScenario.Reminder)
            .SetTag(ReviewTag)
            .SetGroup(ReviewGroup)
            .SetTimeStamp(DateTimeOffset.Now)
            .AddText("WordBase · " + headline);

    private static AppNotificationButton Action(string label, params (string Key, object)[] args)
    {
        var button = new AppNotificationButton(label);
        foreach (var (key, value) in args)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            button = button.AddArgument(key, text);
        }

        return button;
    }

    private static void AppendReading(AppNotificationBuilder builder, string? reading)
    {
        if (!string.IsNullOrWhiteSpace(reading))
        {
            builder.AddText(reading);
        }
    }

    /// <summary>同 Tag+Group 的通知会被系统直接替换，不会堆一屏。</summary>
    private bool ShowReview(AppNotification notification)
    {
        if (!IsUsable)
        {
            return false;
        }

        try
        {
            AppNotificationManager.Default.Show(notification);
            LogPayload(notification.Payload);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("发送通知失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 记录实际下发的 toast 结构。用户反馈「通知自己消失」「按钮没出来」时，
    /// 靠这行日志就能判断是场景没生效还是按钮超限，不用猜。
    /// </summary>
    private void LogPayload(string payload)
    {
        // payload 用单引号属性（scenario='reminder'），两种引号都要认
        var hasReminder = payload.Contains("scenario=\"reminder\"", StringComparison.Ordinal) ||
                          payload.Contains("scenario='reminder'", StringComparison.Ordinal);
        var buttons = System.Text.RegularExpressions.Regex.Matches(payload, "<action ").Count;
        _logger.Info($"通知已下发：scenario={(hasReminder ? "reminder" : "default")}, buttons={buttons}, bytes={payload.Length}");
        if (Environment.GetEnvironmentVariable("WORDBASE_DUMP_TOAST") == "1")
        {
            _logger.Info("toast payload: " + payload);
        }
    }

    internal static string Truncate(string? text, int max)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length <= max)
        {
            return value;
        }

        return value[..Math.Max(1, max - 1)] + "…";
    }

    public async Task<SelfTestResult> RunSelfTestAsync(TimeSpan timeout)
    {
        var diagnostics = Diagnose();
        if (!IsUsable)
        {
            return new SelfTestResult(false,
                "原生通知不可用，建议把通知通道改为「自绘卡片」，或到系统设置里允许 WordBase 通知。", diagnostics);
        }

        _selfTest = new TaskCompletionSource<PromptAction>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var notification = new AppNotificationBuilder()
                .SetScenario(AppNotificationScenario.Reminder)
                .AddText("WordBase 通知自检")
                .AddText("请在 15 秒内点一下下面的按钮，以确认通知真的能被看到。")
                .AddButton(new AppNotificationButton("我看到了").AddArgument("action", "selftest"))
                .BuildNotification();
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
