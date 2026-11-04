using Microsoft.UI.Dispatching;
using WordBase.Core.Models;
using WordBase.Core.Services;

namespace WordBase.App.Services;

/// <summary>
/// 提醒调度。不轮询数据库：只有一个每分钟的轻量定时器，到点才查一次「最近到期」，
/// 其余时间进程完全空闲。免打扰、每日上限、最小间隔、每小时上限都在这里拦。
/// </summary>
public sealed class NotificationScheduler : IDisposable
{
    private readonly ReviewService _review;
    private readonly NotificationService _notifications;
    private readonly AppSettings _settings;
    private readonly AppLogger _logger;
    private readonly DispatcherQueueTimer _timer;
    private readonly Queue<DateTimeOffset> _sentTimestamps = new();

    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;
    private DateOnly _statDay = DateOnly.FromDateTime(DateTime.Now);
    private int _sentToday;
    private PendingPrompt? _pending;

    public NotificationScheduler(ReviewService review, NotificationService notifications, AppSettings settings,
        AppLogger logger, DispatcherQueue dispatcher)
    {
        _review = review;
        _notifications = notifications;
        _settings = settings;
        _logger = logger;

        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick();
    }

    public bool IsPaused { get; private set; }

    /// <summary>原生通知不可用时的兜底展示（自绘卡片）。</summary>
    public Func<Entry, Task>? FallbackPresenter { get; set; }

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

    public void NoteConfirmed(long entryId)
    {
        if (_pending is { } pending && pending.EntryId == entryId)
        {
            NativeConfirmed++;
            _pending = null;
        }
    }

    public void ResetCounters()
    {
        NativeSent = 0;
        NativeConfirmed = 0;
        NativeNoResponse = 0;
    }

    private void Tick()
    {
        var now = DateTimeOffset.Now;

        if (DateOnly.FromDateTime(now.LocalDateTime) != _statDay)
        {
            _statDay = DateOnly.FromDateTime(now.LocalDateTime);
            _sentToday = 0;
        }

        if (_pending is { } pending && now - pending.SentAt > TimeSpan.FromMinutes(10))
        {
            NativeNoResponse++;
            _pending = null;
            _logger.Info($"通知 {pending.EntryId} 在 10 分钟内没有交互回执，记为无响应");
        }

        if (IsPaused || _settings.NotificationsPaused)
        {
            return;
        }

        if (_sentToday >= _settings.DailyLimit)
        {
            return;
        }

        if (now - _lastSentAt < TimeSpan.FromMinutes(_settings.MinMinutesBetweenNotifications))
        {
            return;
        }

        if (!_settings.IsWithinActiveHours(now))
        {
            return;
        }

        while (_sentTimestamps.Count > 0 && now - _sentTimestamps.Peek() > TimeSpan.FromHours(1))
        {
            _sentTimestamps.Dequeue();
        }

        if (_sentTimestamps.Count >= _settings.MaxNotificationsPerHour)
        {
            return;
        }

        var due = _review.GetDue(1);
        if (due.Count == 0)
        {
            return;
        }

        _ = PresentAsync(due[0]);
    }

    public async Task<Entry?> TriggerNowAsync()
    {
        var due = _review.GetDue(1);
        if (due.Count == 0)
        {
            return null;
        }

        await PresentAsync(due[0]).ConfigureAwait(false);
        return due[0];
    }

    private async Task PresentAsync(Entry entry)
    {
        var now = DateTimeOffset.Now;
        _lastSentAt = now;
        _sentTimestamps.Enqueue(now);
        _sentToday++;

        var useNative = _settings.Channel != NotificationChannel.Popup &&
                        _notifications.TryShowReviewPrompt(entry.Id, entry.Term, entry.ReadingsDisplay);

        if (useNative)
        {
            NativeSent++;
            _pending = new PendingPrompt(entry.Id, now);
            _logger.Info($"已发送原生通知：{entry.Term}");
            return;
        }

        _logger.Info($"原生通知不可用，改用自绘卡片：{entry.Term}");
        if (FallbackPresenter is not null)
        {
            await FallbackPresenter(entry).ConfigureAwait(false);
        }
    }

    public void Dispose() => _timer.Stop();

    private sealed record PendingPrompt(long EntryId, DateTimeOffset SentAt);
}
