using WordBase.Core.Services;
using Xunit;

namespace WordBase.Core.Tests;

public class ReminderPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private static ReminderContext Context(
        int dueCount,
        DateTimeOffset? lastSingle = null,
        DateTimeOffset? lastBatch = null,
        bool paused = false,
        bool notificationsPaused = false,
        int sentToday = 0,
        int dailyLimit = 60,
        bool withinActiveHours = true,
        int sentLastHour = 0,
        int maxPerHour = 6,
        DateTimeOffset? snoozedUntil = null) =>
        new(dueCount, Now, lastSingle, lastBatch,
            TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60),
            paused, notificationsPaused, sentToday, dailyLimit, withinActiveHours, sentLastHour, maxPerHour,
            snoozedUntil);

    [Fact]
    public void NoDue_ProducesNothing()
    {
        var decision = ReminderPolicy.Decide(Context(0));
        Assert.Equal(ReminderKind.None, decision.Kind);
        Assert.False(decision.ShouldPresent);
    }

    [Fact]
    public void SingleDue_IsSingleWordReminder()
    {
        var decision = ReminderPolicy.Decide(Context(1));
        Assert.Equal(ReminderKind.SingleWord, decision.Kind);
        Assert.True(decision.ShouldPresent);
        Assert.Null(decision.BlockedReason);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(120)]
    public void MultipleDue_BatchesIntoOneReminder(int dueCount)
    {
        var decision = ReminderPolicy.Decide(Context(dueCount));
        Assert.Equal(ReminderKind.Batch, decision.Kind);
        Assert.True(decision.ShouldPresent);
    }

    [Fact]
    public void SingleWord_RespectsMinimumGap()
    {
        var decision = ReminderPolicy.Decide(Context(1, lastSingle: Now.AddMinutes(-5)));
        Assert.Equal(ReminderKind.SingleWord, decision.Kind);
        Assert.False(decision.ShouldPresent);
        Assert.Contains("不足 15 分钟", decision.BlockedReason);
    }

    [Fact]
    public void SingleWord_AllowsAfterGap()
    {
        var decision = ReminderPolicy.Decide(Context(1, lastSingle: Now.AddMinutes(-16)));
        Assert.True(decision.ShouldPresent);
    }

    [Fact]
    public void Batch_UsesLongerGapThanSingleWord()
    {
        // 20 分钟前刚发过单词提醒：批量提醒不受影响（各自独立节流）
        var allowed = ReminderPolicy.Decide(Context(3, lastSingle: Now.AddMinutes(-20)));
        Assert.True(allowed.ShouldPresent);

        // 但批量提醒自己有 60 分钟间隔
        var blocked = ReminderPolicy.Decide(Context(3, lastBatch: Now.AddMinutes(-20)));
        Assert.False(blocked.ShouldPresent);
        Assert.Contains("不足 60 分钟", blocked.BlockedReason);
    }

    [Fact]
    public void Snooze_BlocksEverything()
    {
        var decision = ReminderPolicy.Decide(Context(1, snoozedUntil: Now.AddMinutes(4)));
        Assert.False(decision.ShouldPresent);
        Assert.Contains("稍后", decision.BlockedReason);
    }

    [Fact]
    public void Snooze_Expires()
    {
        var decision = ReminderPolicy.Decide(Context(1, snoozedUntil: Now.AddMinutes(-1)));
        Assert.True(decision.ShouldPresent);
    }

    [Fact]
    public void Paused_Blocks()
    {
        Assert.False(ReminderPolicy.Decide(Context(1, paused: true)).ShouldPresent);
        Assert.False(ReminderPolicy.Decide(Context(1, notificationsPaused: true)).ShouldPresent);
    }

    [Fact]
    public void DailyLimit_Blocks()
    {
        var decision = ReminderPolicy.Decide(Context(1, sentToday: 60, dailyLimit: 60));
        Assert.False(decision.ShouldPresent);
        Assert.Contains("每日上限", decision.BlockedReason);
    }

    [Fact]
    public void OutsideActiveHours_Blocks()
    {
        var decision = ReminderPolicy.Decide(Context(1, withinActiveHours: false));
        Assert.False(decision.ShouldPresent);
        Assert.Contains("时段", decision.BlockedReason);
    }

    [Fact]
    public void HourlyCap_Blocks()
    {
        var decision = ReminderPolicy.Decide(Context(1, sentLastHour: 6, maxPerHour: 6));
        Assert.False(decision.ShouldPresent);
        Assert.Contains("每小时上限", decision.BlockedReason);
    }

    [Fact]
    public void BlockedDecision_StillReportsTheKind()
    {
        var decision = ReminderPolicy.Decide(Context(5, paused: true));
        Assert.Equal(ReminderKind.Batch, decision.Kind);
        Assert.False(decision.ShouldPresent);
    }
}
