using Microsoft.UI.Dispatching;
using WordBase.Core.Models;
using WordBase.Core.Scheduling;
using WordBase.Core.Services;

namespace WordBase.App.Services;

/// <summary>
/// 提醒调度。不轮询数据库：只有一个每分钟的轻量定时器，到点才查一次「最近到期」，
/// 其余时间进程完全空闲。
///
/// 到期数量决定形态：
///   1 个  → 一词一条通知，可直接在通知里完成问答（不问主界面）
///   多个  → 一条汇总通知，点开在主界面一次做完
/// 通知用 Reminder 场景常驻，5 分钟无人理再主动收起。
/// </summary>
public sealed class NotificationScheduler : IDisposable
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CompletedDwell = TimeSpan.FromSeconds(3);

    private readonly ReviewService _review;
    private readonly NotificationService _notifications;
    private readonly AppSettings _settings;
    private readonly AppLogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly Queue<DateTimeOffset> _sentTimestamps = new();

    private DateTimeOffset? _lastSingleSentAt;
    private DateTimeOffset? _lastBatchSentAt;
    private DateTimeOffset? _snoozedUntil;
    private DateTimeOffset _pendingSince = DateTimeOffset.MinValue;
    private DateTimeOffset? _removeAt;
    private DateOnly _statDay = DateOnly.FromDateTime(DateTime.Now);
    private int _sentToday;
    private bool _pendingAwaitingReply;
    private string? _lastBlockedReason;

    public NotificationScheduler(ReviewService review, NotificationService notifications, AppSettings settings,
        AppLogger logger, DispatcherQueue dispatcher)
    {
        _review = review;
        _notifications = notifications;
        _settings = settings;
        _logger = logger;
        _dispatcher = dispatcher;

        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick();
    }

    public bool IsPaused { get; private set; }

    /// <summary>原生通知不可用时的兜底展示（自绘卡片，单词）。</summary>
    public Func<Entry, bool, Task>? FallbackPresenter { get; set; }

    /// <summary>原生通知不可用时的兜底展示（自绘卡片，批量汇总）。</summary>
    public Func<int, Task>? FallbackSummaryPresenter { get; set; }

    /// <summary>打开主界面的复习页；entryId 为空表示整批。</summary>
    public Action<long?, bool?>? OpenReviewRequested { get; set; }

    public int SentToday => _sentToday;
    public int NativeSent { get; private set; }
    public int NativeConfirmed { get; private set; }
    public int NativeNoResponse { get; private set; }

    public void Start()
    {
        _timer.Start();
        _logger.Info("提醒调度已启动");
    }

    public void Pause(bool paused)
    {
        IsPaused = paused;
        _logger.Info(paused ? "提醒已暂停" : "提醒已恢复");
    }

    public void ResetCounters()
    {
        NativeSent = 0;
        NativeConfirmed = 0;
        NativeNoResponse = 0;
    }

    /// <summary>用户点了「稍后」：10 分钟内不再打扰。</summary>
    public void Snooze(TimeSpan duration)
    {
        _snoozedUntil = DateTimeOffset.Now + duration;
        _pendingAwaitingReply = false;
        _logger.Info($"已稍后 {duration.TotalMinutes:0} 分钟");
        _ = _notifications.RemoveReviewAsync();
    }

    // ---------------- 定时检查 ----------------

    private void Tick()
    {
        var now = DateTimeOffset.Now;

        if (DateOnly.FromDateTime(now.LocalDateTime) != _statDay)
        {
            _statDay = DateOnly.FromDateTime(now.LocalDateTime);
            _sentToday = 0;
        }

        // 「已完成」短暂停留后收起
        if (_removeAt is { } removeAt && now >= removeAt)
        {
            _removeAt = null;
            _ = _notifications.RemoveReviewAsync();
        }

        // 常驻通知无人理会：超过寿命就收起，并记一次无响应
        if (_pendingAwaitingReply && now - _pendingSince > PendingLifetime)
        {
            _pendingAwaitingReply = false;
            NativeNoResponse++;
            _logger.Info("复习通知在 5 分钟内没有交互回执，已收起并记为无响应");
            _removeAt = now;
        }

        while (_sentTimestamps.Count > 0 && now - _sentTimestamps.Peek() > TimeSpan.FromHours(1))
        {
            _sentTimestamps.Dequeue();
        }

        var dueCount = _review.CountDue();
        var decision = ReminderPolicy.Decide(new ReminderContext(
            DueCount: dueCount,
            Now: now,
            LastSingleSentAt: _lastSingleSentAt,
            LastBatchSentAt: _lastBatchSentAt,
            MinGap: TimeSpan.FromMinutes(_settings.MinMinutesBetweenNotifications),
            BatchGap: TimeSpan.FromMinutes(_settings.BatchReminderMinutes),
            Paused: IsPaused,
            NotificationsPaused: _settings.NotificationsPaused,
            SentToday: _sentToday,
            DailyLimit: _settings.DailyLimit,
            WithinActiveHours: _settings.IsWithinActiveHours(now),
            SentLastHour: _sentTimestamps.Count,
            MaxPerHour: _settings.MaxNotificationsPerHour,
            SnoozedUntil: _snoozedUntil));

        if (decision.Kind == ReminderKind.None)
        {
            _lastBlockedReason = null;
            return;
        }

        if (decision.BlockedReason is { } reason)
        {
            if (reason != _lastBlockedReason)
            {
                _lastBlockedReason = reason;
                _logger.Info($"有 {dueCount} 个词到期，但本次提醒被拦下：{reason}");
            }

            return;
        }

        _lastBlockedReason = null;
        Present(decision.Kind, dueCount);
    }

    private void Present(ReminderKind kind, int dueCount)
    {
        var now = DateTimeOffset.Now;
        _sentToday++;
        _sentTimestamps.Enqueue(now);

        if (kind == ReminderKind.SingleWord)
        {
            _lastSingleSentAt = now;
            var due = _review.GetDue(1);
            if (due.Count == 0)
            {
                return;
            }

            var entry = due[0];
            var showReading = _settings.ShowReadingInReview;
            var mode = _settings.DirectQuizProbability;

            if (_settings.Channel == NotificationChannel.Popup)
            {
                PresentSingleWithCard(entry, showReading);
                return;
            }

            var presented = Random.Shared.NextDouble() < mode
                ? TryShowQuizNotification(entry, showReading)
                : _notifications.TryShowAskKnown(entry.Id, entry.Term, showReading ? entry.ReadingsDisplay : null);

            if (presented)
            {
                NativeSent++;
                MarkPending();
                _logger.Info($"已发送单词通知：{entry.Term}（{(mode > 0 ? "按概率可能出选择题" : "只问认识")}）");
                return;
            }

            PresentSingleWithCard(entry, showReading);
            return;
        }

        _lastBatchSentAt = now;
        if (_settings.Channel == NotificationChannel.Popup)
        {
            PresentBatchWithCard(dueCount);
            return;
        }

        if (_notifications.TryShowSummary(dueCount))
        {
            NativeSent++;
            MarkPending();
            _logger.Info($"已发送汇总通知：{dueCount} 个词到期");
            return;
        }

        PresentBatchWithCard(dueCount);
    }

    private bool TryShowQuizNotification(Entry entry, bool showReading)
    {
        var question = _review.BuildQuiz(entry, NotificationService.MaxButtons - 1, Random.Shared);
        if (question is null)
        {
            return false;
        }

        var choices = question.Options
            .Select(o => new QuizChoice(o.Text, o.IsCorrect, o.SourceEntryId))
            .ToList();

        return _notifications.TryShowQuiz(entry.Id, entry.Term, showReading ? entry.ReadingsDisplay : null, choices);
    }

    private void PresentSingleWithCard(Entry entry, bool showReading)
    {
        if (FallbackPresenter is null)
        {
            return;
        }

        MarkPending();
        _ = FallbackPresenter(entry, showReading);
    }

    private void PresentBatchWithCard(int dueCount)
    {
        if (FallbackSummaryPresenter is null)
        {
            return;
        }

        MarkPending();
        _ = FallbackSummaryPresenter(dueCount);
    }

    private void MarkPending()
    {
        _pendingAwaitingReply = true;
        _pendingSince = DateTimeOffset.Now;
        _removeAt = null;
    }

    // ---------------- 用户操作 ----------------

    /// <summary>处理通知/卡片上的按钮。必须在 UI 线程调用。</summary>
    public void Handle(PromptAction action)
    {
        switch (action.Action)
        {
            case "snooze":
                Snooze(TimeSpan.FromMinutes(_settings.SnoozeMinutes));
                return;
            case "startBulk":
                _pendingAwaitingReply = false;
                _ = _notifications.RemoveReviewAsync();
                OpenReviewRequested?.Invoke(null, null);
                return;
            case "open":
                _pendingAwaitingReply = false;
                _ = _notifications.RemoveReviewAsync();
                OpenReviewRequested?.Invoke(action.EntryId, null);
                return;
            case "dismiss":
                Complete();
                return;
        }

        if (action.EntryId is not { } entryId)
        {
            OpenReviewRequested?.Invoke(null, null);
            return;
        }

        var entry = _review.GetEntry(entryId);
        if (entry is null)
        {
            _logger.Warn($"通知引用的词条 {entryId} 已不存在，收起通知");
            Complete();
            return;
        }

        switch (action.Action)
        {
            case "known":
                Submit(entry, Interaction.KnownNotQuizzed);
                Complete();
                break;

            case "unknown":
                RevealAnswer(entry, Interaction.NotKnown);
                break;

            case "quiz":
                if (action.IsCorrect)
                {
                    Submit(entry, Interaction.KnownAndCorrect);
                    Complete();
                }
                else
                {
                    RevealAnswer(entry, Interaction.KnownButWrong, action.ConfusedWith);
                }

                break;

            case "rate":
                Submit(entry,
                    action.BaseInteraction == "wrong" ? Interaction.KnownButWrong : Interaction.NotKnown,
                    action.Rating,
                    action.ConfusedWith);
                Complete();
                break;

            default:
                Complete();
                break;
        }
    }

    private void RevealAnswer(Entry entry, Interaction interaction, long? confusedWith = null)
    {
        NativeConfirmed++;

        var lines = new List<string>();
        foreach (var sense in entry.Senses.Where(s => !string.IsNullOrWhiteSpace(s.Gloss)).Take(2))
        {
            var reading = entry.Readings.FirstOrDefault(r => r.Id == sense.ReadingId)?.Text;
            lines.Add(string.IsNullOrEmpty(reading) ? sense.Gloss : $"{reading} · {sense.Gloss}");
        }

        foreach (var example in entry.Examples.Where(e => !string.IsNullOrWhiteSpace(e.Text)).Take(1))
        {
            lines.Add(example.Text);
        }

        var baseInteraction = interaction == Interaction.KnownButWrong ? "wrong" : "unknown";
        var shown = _notifications.TryShowAnswer(entry.Id, entry.Term, lines, withRatings: true, baseInteraction,
            confusedWith);

        if (!shown)
        {
            // 原生不可用就直接在主界面展开，让用户看完整内容
            OpenReviewRequested?.Invoke(entry.Id, false);
            return;
        }

        MarkPending();
    }

    private void Submit(Entry entry, Interaction interaction, Rating? selfRating = null, long? confusedWith = null)
    {
        try
        {
            NativeConfirmed++;
            _review.Submit(entry, interaction, selfRating, responseMs: 0, confusedWith);
            _logger.Info($"通知结算：{entry.Term} → {interaction} / {selfRating?.ToString() ?? "auto"}");
        }
        catch (Exception ex)
        {
            _logger.Error("通知结算失败", ex);
        }
    }

    private void Complete()
    {
        _pendingAwaitingReply = false;

        if (_settings.Channel == NotificationChannel.Popup)
        {
            return;
        }

        if (_notifications.TryShowCompleted())
        {
            _removeAt = DateTimeOffset.Now + CompletedDwell;
        }
        else
        {
            _ = _notifications.RemoveReviewAsync();
        }
    }

    /// <summary>供主界面「开始复习」复用：手动复习不算提醒，重置节流。</summary>
    public void NoteManualReviewStarted()
    {
        _pendingAwaitingReply = false;
        _removeAt = null;
        _ = _notifications.RemoveReviewAsync();
    }

    public void Dispose() => _timer.Stop();
}
