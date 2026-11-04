using WordBase.Core.Models;

namespace WordBase.Core.Scheduling;

/// <summary>
/// FSRS-5 的记忆模型部分（纯数学，无状态）。权重顺序与官方 19 个参数一致。
/// 参考：https://github.com/open-spaced-repetition/free-spaced-repetition-scheduler
/// </summary>
public sealed class FsrsParameters
{
    public static readonly double[] Default =
    {
        0.40255, 1.18385, 3.173, 15.69105, 7.1949,
        0.5345, 1.4604, 0.0046, 1.54575, 0.1192,
        1.01925, 1.9395, 0.11, 0.29605, 2.2698,
        0.2315, 2.9898, 0.51655, 0.6621,
    };

    public double[] Weights { get; init; } = Default;

    public double W(int index) => Weights[index];

    /// <summary>期望回忆曲线常数，使 R(S, S) = 0.9。</summary>
    public static double Factor => Math.Pow(0.9, 1.0 / Decay) - 1.0;

    public const double Decay = -0.5;

    public double InitialStability(Rating rating) => Math.Max(0.1, W((int)rating - 1));

    public double InitialDifficulty(Rating rating) =>
        Clamp(W(4) - Math.Exp(W(5) * ((int)rating - 1)) + 1.0, 1.0, 10.0);

    public double NextDifficulty(double difficulty, Rating rating) =>
        Clamp(W(7) * InitialDifficulty(Rating.Easy) +
               (1.0 - W(7)) * (difficulty - W(6) * ((int)rating - 3)),
            1.0, 10.0);

    public double Retrievability(double elapsedDays, double stability)
    {
        if (stability <= 0)
        {
            return 0;
        }

        var elapsed = Math.Max(0, elapsedDays);
        return Math.Pow(1.0 + Factor * elapsed / stability, Decay);
    }

    /// <summary>由稳定度换算间隔（天），按目标保持率求逆。</summary>
    public double IntervalDays(double stability, double desiredRetention)
    {
        var retention = Math.Clamp(desiredRetention, 0.7, 0.99);
        return stability / Factor * (Math.Pow(retention, 1.0 / Decay) - 1.0);
    }

    public double NextRecallStability(double difficulty, double stability, double retrievability, Rating rating)
    {
        var hardPenalty = rating == Rating.Hard ? W(15) : 1.0;
        var easyBonus = rating == Rating.Easy ? W(16) : 1.0;
        return stability * (1.0 +
                            Math.Exp(W(8)) *
                            (11.0 - difficulty) *
                            Math.Pow(stability, -W(9)) *
                            (Math.Exp(W(10) * (1.0 - retrievability)) - 1.0) *
                            hardPenalty * easyBonus);
    }

    public double NextForgetStability(double difficulty, double stability, double retrievability) =>
        W(11) *
        Math.Pow(difficulty, -W(12)) *
        (Math.Pow(stability + 1.0, W(13)) - 1.0) *
        Math.Exp(W(14) * (1.0 - retrievability));

    /// <summary>同日（短周期）复习的稳定度更新。</summary>
    public double NextShortTermStability(double stability, Rating rating) =>
        stability * Math.Exp(W(17) * ((int)rating - 3 + W(18)));

    public static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) ? min : Math.Min(max, Math.Max(min, value));
}
