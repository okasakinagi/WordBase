using WordBase.Core.Models;

namespace WordBase.Core.Scheduling;

/// <summary>
/// 把「认识/不认识 + 选择题结果」换算成置信度，再换算成 FSRS 评分。
/// 置信度原值会落库（review_log.confidence），将来可无损换成软标签训练。
/// </summary>
public sealed class ConfidenceProfile
{
    public double KnownAndCorrect { get; init; } = 1.0;
    public double KnownNotQuizzed { get; init; } = 0.7;
    public double KnownButWrong { get; init; } = 0.2;
    public double NotKnown { get; init; } = 0.1;

    public static readonly ConfidenceProfile Default = new();

    public double For(Interaction interaction) => interaction switch
    {
        Interaction.KnownAndCorrect => KnownAndCorrect,
        Interaction.KnownNotQuizzed => KnownNotQuizzed,
        Interaction.KnownButWrong => KnownButWrong,
        _ => NotKnown,
    };

    /// <summary>离散映射。Easy 只留给用户主动选择的「很轻松」。</summary>
    public Rating ToRating(double confidence, Rating? explicitRating = null)
    {
        if (explicitRating is not null)
        {
            return explicitRating.Value;
        }

        return confidence switch
        {
            >= 0.95 => Rating.Good,
            >= 0.45 => Rating.Hard,
            _ => Rating.Again,
        };
    }
}

public enum Interaction
{
    /// <summary>认识，且被抽中做选择题并答对。</summary>
    KnownAndCorrect = 0,

    /// <summary>认识，但这次没被抽中做选择题。</summary>
    KnownNotQuizzed = 1,

    /// <summary>认识，但选择题答错了（大概率是有点印象 / 记成了别的词）。</summary>
    KnownButWrong = 2,

    /// <summary>主动点「不认识」。</summary>
    NotKnown = 3,
}
