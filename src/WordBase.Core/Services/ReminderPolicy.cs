namespace WordBase.Core.Services;

public enum ReminderKind
{
    None = 0,

    /// <summary>只有一个词到期：单独提醒这一个。</summary>
    SingleWord = 1,

    /// <summary>多个词到期：汇总成一条，点开在主界面一次做完。</summary>
    Batch = 2,
}

public sealed record ReminderContext(
    int DueCount,
    DateTimeOffset Now,
    DateTimeOffset? LastSingleSentAt,
    DateTimeOffset? LastBatchSentAt,
    TimeSpan MinGap,
    TimeSpan BatchGap,
    bool Paused,
    bool NotificationsPaused,
    int SentToday,
    int DailyLimit,
    bool WithinActiveHours,
    int SentLastHour,
    int MaxPerHour,
    DateTimeOffset? SnoozedUntil);

public sealed record ReminderDecision(ReminderKind Kind, string? BlockedReason)
{
    public bool ShouldPresent => Kind != ReminderKind.None && BlockedReason is null;
}

/// <summary>
/// 提醒的准入判断（纯函数，便于单测）。
/// 规则：到期 1 个按「一词一条」，多个按「汇总一条」；两者各自有节流间隔，
/// 批量汇总用更长间隔，避免整天反复弹同一件事。
/// </summary>
public static class ReminderPolicy
{
    public static ReminderDecision Decide(ReminderContext context)
    {
        if (context.DueCount <= 0)
        {
            return new ReminderDecision(ReminderKind.None, null);
        }

        var kind = context.DueCount == 1 ? ReminderKind.SingleWord : ReminderKind.Batch;

        if (context.SnoozedUntil is { } snooze && context.Now < snooze)
        {
            return new ReminderDecision(kind, $"用户选择了稍后（{snooze:HH:mm} 之后再提醒）");
        }

        if (context.Paused || context.NotificationsPaused)
        {
            return new ReminderDecision(kind, "提醒已暂停");
        }

        if (context.SentToday >= context.DailyLimit)
        {
            return new ReminderDecision(kind, $"已达到每日上限（{context.SentToday}/{context.DailyLimit}）");
        }

        if (!context.WithinActiveHours)
        {
            return new ReminderDecision(kind, "不在允许提醒的时段");
        }

        if (context.SentLastHour >= context.MaxPerHour)
        {
            return new ReminderDecision(kind, $"已达每小时上限（{context.MaxPerHour}）");
        }

        var gap = kind == ReminderKind.SingleWord ? context.MinGap : context.BatchGap;
        var last = kind == ReminderKind.SingleWord ? context.LastSingleSentAt : context.LastBatchSentAt;

        if (last is { } lastSent && context.Now - lastSent < gap)
        {
            return new ReminderDecision(kind,
                $"距上次提醒不足 {Math.Max(1, (int)gap.TotalMinutes)} 分钟");
        }

        return new ReminderDecision(kind, null);
    }
}
