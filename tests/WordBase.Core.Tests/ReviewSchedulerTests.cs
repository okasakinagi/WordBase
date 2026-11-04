using WordBase.Core.Models;
using WordBase.Core.Scheduling;
using Xunit;

namespace WordBase.Core.Tests;

public class ReviewSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 0, 0, TimeSpan.FromHours(8));

    private static ReviewState Advance(ReviewScheduler scheduler, ReviewState state, Rating rating, int days)
    {
        var at = (state.DueAt ?? Now).AddDays(days);
        return scheduler.Review(state, rating, at).State;
    }

    [Fact]
    public void NewCard_Again_IsDueInMinutes()
    {
        var scheduler = new ReviewScheduler();
        var state = scheduler.CreateNew(Now);
        var result = scheduler.Review(state, Rating.Again, Now);

        Assert.Equal(CardState.Learning, result.State.State);
        Assert.True(result.State.DueAt <= Now.AddMinutes(30));
        Assert.Equal(0, result.IntervalDays);
    }

    [Fact]
    public void NewCard_Good_SchedulesAtLeastOneDay()
    {
        var scheduler = new ReviewScheduler();
        var result = scheduler.Review(scheduler.CreateNew(Now), Rating.Good, Now);

        Assert.Equal(CardState.Review, result.State.State);
        Assert.True(result.IntervalDays >= 1);
        Assert.True(result.State.DueAt > Now.AddHours(12));
    }

    [Fact]
    public void Graduation_FromLearning_EntersReview()
    {
        var scheduler = new ReviewScheduler();
        var learning = scheduler.Review(scheduler.CreateNew(Now), Rating.Again, Now).State;
        var graduated = scheduler.Review(learning, Rating.Good, Now.AddMinutes(10)).State;

        Assert.Equal(CardState.Review, graduated.State);
        Assert.NotNull(graduated.DueAt);
        Assert.True(graduated.ScheduledDays >= 1);
    }

    [Fact]
    public void RatingOrder_ProducesIncreasingIntervals()
    {
        var scheduler = new ReviewScheduler();
        var hard = scheduler.Review(scheduler.CreateNew(Now), Rating.Hard, Now);
        var good = scheduler.Review(scheduler.CreateNew(Now), Rating.Good, Now);
        var easy = scheduler.Review(scheduler.CreateNew(Now), Rating.Easy, Now);

        Assert.True(easy.IntervalDays > good.IntervalDays);
        Assert.True(good.IntervalDays >= hard.IntervalDays);
    }

    [Fact]
    public void StableCard_Good_ExtendsInterval()
    {
        var scheduler = new ReviewScheduler();
        var state = scheduler.Review(scheduler.CreateNew(Now), Rating.Good, Now).State;
        var first = state.ScheduledDays;

        var next = scheduler.Review(state, Rating.Good, Now.AddDays(first)).State;
        Assert.True(next.ScheduledDays > first);
    }

    [Fact]
    public void Lapse_KeepsIntervalAboveFloor()
    {
        var scheduler = new ReviewScheduler();
        var state = scheduler.Review(scheduler.CreateNew(Now), Rating.Easy, Now).State;
        for (var i = 0; i < 5; i++)
        {
            state = Advance(scheduler, state, Rating.Good, state.ScheduledDays);
        }

        var before = state.Stability;
        var result = scheduler.Review(state, Rating.Again, Now.AddDays(30));

        Assert.Equal(CardState.Relearning, result.State.State);
        Assert.True(result.State.Stability >= before * scheduler.Options.MinLapseRetention * 0.999);
        Assert.Equal(1, result.State.Lapses);
    }

    [Fact]
    public void Interval_IsCappedByMaximum()
    {
        var scheduler = new ReviewScheduler(new SchedulerOptions { MaximumIntervalDays = 30 });
        var state = scheduler.Review(scheduler.CreateNew(Now), Rating.Easy, Now).State;
        for (var i = 0; i < 8; i++)
        {
            state = Advance(scheduler, state, Rating.Easy, state.ScheduledDays);
            Assert.True(state.ScheduledDays <= 30);
        }
    }

    [Fact]
    public void Retrievability_DecaysOverTime()
    {
        var fsrs = new FsrsParameters();
        var near = fsrs.Retrievability(1, 10);
        var far = fsrs.Retrievability(30, 10);
        Assert.True(near > far);
        Assert.InRange(fsrs.Retrievability(10, 10), 0.89, 0.91);
    }
}
