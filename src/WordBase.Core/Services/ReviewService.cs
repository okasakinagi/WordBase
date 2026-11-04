using WordBase.Core.Abstractions;
using WordBase.Core.Models;
using WordBase.Core.Quizzing;
using WordBase.Core.Scheduling;

namespace WordBase.Core.Services;

public sealed class ReviewOutcome
{
    public required ReviewState State { get; init; }
    public required Rating Rating { get; init; }
    public required double Confidence { get; init; }
    public required Interaction Interaction { get; init; }
    public DateTimeOffset DueAt => State.DueAt ?? DateTimeOffset.Now;
}

public sealed class ReviewService
{
    private readonly IEntryStore _store;
    private readonly ReviewScheduler _scheduler;
    private readonly ConfidenceProfile _confidence;
    private readonly Func<DateTimeOffset> _clock;

    public ReviewService(IEntryStore store, ReviewScheduler? scheduler = null,
        ConfidenceProfile? confidence = null, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _scheduler = scheduler ?? new ReviewScheduler();
        _confidence = confidence ?? ConfidenceProfile.Default;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public ConfidenceProfile Confidence => _confidence;
    public ReviewScheduler Scheduler => _scheduler;

    public IReadOnlyList<Entry> GetDue(int limit = 20) => _store.GetDue(_clock(), limit);

    public int CountDue() => _store.CountDue(_clock());

    public EntryStats Stats() => _store.GetStats(_clock());

    public QuizQuestion? BuildQuiz(Entry entry, int optionCount = 4, Random? rng = null)
    {
        var distractors = _store.GetDistractorOptions(entry.Language, entry.Id, 40);
        return QuizBuilder.Build(entry, distractors, optionCount, rng);
    }

    /// <summary>
    /// 认识且未被抽中时用 <paramref name="samplingProbability"/> 决定是否出选择题。
    /// 抽中与否由调用方决定，这里只负责结算。
    /// </summary>
    public ReviewOutcome Submit(Entry entry, Interaction interaction, Rating? selfRating = null,
        int responseMs = 0, long? confusedWithEntryId = null)
    {
        var now = _clock();
        var confidence = _confidence.For(interaction);
        var rating = _confidence.ToRating(confidence, selfRating);
        var prev = entry.Review ?? _scheduler.CreateNew(now);
        var result = _scheduler.Review(prev, rating, now);

        _store.SaveReview(result.State, new ReviewLog
        {
            EntryId = entry.Id,
            ReviewedAt = now,
            Phase = interaction.ToString(),
            Confidence = confidence,
            Rating = rating,
            PrevState = prev.State,
            PrevStability = prev.Stability,
            PrevDifficulty = prev.Difficulty,
            NextStability = result.State.Stability,
            NextDifficulty = result.State.Difficulty,
            NextDueAt = result.State.DueAt ?? now,
            ResponseMs = responseMs,
        });

        if (interaction == Interaction.KnownButWrong && confusedWithEntryId is { } other)
        {
            _store.RecordConfusion(entry.Id, other, now);
        }

        entry.Review = result.State;
        return new ReviewOutcome
        {
            State = result.State,
            Rating = rating,
            Confidence = confidence,
            Interaction = interaction,
        };
    }
}
