using WordBase.Core.Models;

namespace WordBase.Core.Scheduling;

public sealed class SchedulerOptions
{
    /// <summary>目标保持率：0.9 表示每个词到期时你能记住 90%。</summary>
    public double DesiredRetention { get; init; } = 0.9;

    public int MaximumIntervalDays { get; init; } = 3650;

    /// <summary>「不认识」后的再现间隔（分钟）。</summary>
    public double RelearnMinutes { get; init; } = 10;

    /// <summary>「模糊」在短周期阶段的间隔（分钟）。</summary>
    public double LearningHardMinutes { get; init; } = 30;

    /// <summary>
    /// 遗忘造成的稳定度下降上限：新稳定度不低于旧稳定度 × 该系数。
    /// 防止一次手滑把间隔直接打回一天（"降幅封顶"）。
    /// </summary>
    public double MinLapseRetention { get; init; } = 0.25;

    public FsrsParameters Parameters { get; init; } = new();
}

public sealed record ScheduleResult(ReviewState State, int IntervalDays, double Retrievability);

/// <summary>把 FSRS 数学部分包装成「卡片状态机 + 到期时间」。</summary>
public sealed class ReviewScheduler
{
    private readonly SchedulerOptions _options;
    private readonly FsrsParameters _fsrs;

    public ReviewScheduler(SchedulerOptions? options = null)
    {
        _options = options ?? new SchedulerOptions();
        _fsrs = _options.Parameters;
    }

    public SchedulerOptions Options => _options;

    public ReviewState CreateNew(DateTimeOffset now, bool dueImmediately = true) => new()
    {
        State = CardState.New,
        DueAt = dueImmediately ? now : null,
        Stability = 0,
        Difficulty = 0,
    };

    public ScheduleResult Review(ReviewState state, Rating rating, DateTimeOffset now)
    {
        var prev = state.Clone();
        var result = state.State switch
        {
            CardState.New => OnNew(prev, rating, now),
            CardState.Learning or CardState.Relearning => OnLearning(prev, rating, now),
            _ => OnReview(prev, rating, now),
        };

        result.State.Reps = prev.Reps + 1;
        result.State.LastReviewAt = now;
        result.State.ElapsedDays = prev.LastReviewAt is null
            ? 0
            : Math.Max(0, (int)Math.Floor((now - prev.LastReviewAt.Value).TotalDays));
        result.State.ScheduledDays = result.IntervalDays;
        result.State.DueAt = result.IntervalDays > 0
            ? now.AddDays(result.IntervalDays)
            : result.State.DueAt;
        return result;
    }

    private ScheduleResult OnNew(ReviewState prev, Rating rating, DateTimeOffset now)
    {
        var stability = _fsrs.InitialStability(rating);
        var difficulty = _fsrs.InitialDifficulty(rating);

        if (rating == Rating.Again)
        {
            return ShortTerm(prev, rating, now, stability, difficulty, CardState.Learning);
        }

        var interval = ClampInterval(_fsrs.IntervalDays(stability, _options.DesiredRetention));
        var next = prev.Clone();
        next.State = CardState.Review;
        next.Stability = stability;
        next.Difficulty = difficulty;
        return new ScheduleResult(next, interval, 1.0);
    }

    private ScheduleResult OnLearning(ReviewState prev, Rating rating, DateTimeOffset now)
    {
        var stability = Math.Max(0.1, prev.Stability);
        var difficulty = prev.Difficulty <= 0 ? _fsrs.InitialDifficulty(rating) : prev.Difficulty;

        if (rating == Rating.Again)
        {
            return ShortTerm(prev, rating, now, _fsrs.NextShortTermStability(stability, rating), difficulty,
                CardState.Learning);
        }

        var boosted = _fsrs.NextShortTermStability(stability, rating);
        var next = prev.Clone();
        next.State = CardState.Review;
        next.Stability = boosted;
        next.Difficulty = _fsrs.NextDifficulty(difficulty, rating);
        var interval = ClampInterval(_fsrs.IntervalDays(boosted, _options.DesiredRetention));
        return new ScheduleResult(next, interval, 1.0);
    }

    private ScheduleResult OnReview(ReviewState prev, Rating rating, DateTimeOffset now)
    {
        var stability = Math.Max(0.1, prev.Stability);
        var difficulty = prev.Difficulty <= 0 ? _fsrs.InitialDifficulty(rating) : prev.Difficulty;

        var elapsedDays = prev.LastReviewAt is null
            ? Math.Max(1, prev.ScheduledDays)
            : Math.Max(0, (now - prev.LastReviewAt.Value).TotalDays);
        var retrievability = _fsrs.Retrievability(elapsedDays, stability);

        if (rating == Rating.Again)
        {
            var forgotten = _fsrs.NextForgetStability(difficulty, stability, retrievability);
            // 降幅封顶
            var floor = stability * _options.MinLapseRetention;
            var nextStability = Math.Max(0.1, Math.Max(forgotten, floor));

            var next = prev.Clone();
            next.State = CardState.Relearning;
            next.Stability = nextStability;
            next.Difficulty = _fsrs.NextDifficulty(difficulty, rating);
            next.Lapses = prev.Lapses + 1;
            next.DueAt = now.AddMinutes(_options.RelearnMinutes);

            return new ScheduleResult(next, 0, retrievability);
        }

        var nextRecall = _fsrs.NextRecallStability(difficulty, stability, retrievability, rating);
        var interval = ClampInterval(_fsrs.IntervalDays(nextRecall, _options.DesiredRetention));

        var result = prev.Clone();
        result.State = CardState.Review;
        result.Stability = nextRecall;
        result.Difficulty = _fsrs.NextDifficulty(difficulty, rating);
        return new ScheduleResult(result, interval, retrievability);
    }

    private ScheduleResult ShortTerm(ReviewState prev, Rating rating, DateTimeOffset now, double stability,
        double difficulty, CardState state)
    {
        var minutes = rating switch
        {
            Rating.Again => _options.RelearnMinutes,
            Rating.Hard => _options.LearningHardMinutes,
            _ => Math.Max(_options.RelearnMinutes, 60),
        };

        var next = prev.Clone();
        next.State = state;
        next.Stability = Math.Max(0.1, stability);
        next.Difficulty = difficulty;
        next.DueAt = now.AddMinutes(minutes);
        return new ScheduleResult(next, 0, 1.0);
    }

    private int ClampInterval(double days) =>
        Math.Clamp((int)Math.Round(days, MidpointRounding.AwayFromZero), 1, _options.MaximumIntervalDays);
}
