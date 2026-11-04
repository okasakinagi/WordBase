using WordBase.Core.Models;

namespace WordBase.Core.Quizzing;

public sealed record QuizOption(string Text, bool IsCorrect);

public sealed class QuizQuestion
{
    public required Entry Entry { get; init; }
    public required string Prompt { get; init; }
    public string? Reading { get; init; }
    public required IReadOnlyList<QuizOption> Options { get; init; }

    public int CorrectIndex => Options.ToList().FindIndex(o => o.IsCorrect);
}

public static class QuizBuilder
{
    /// <summary>从词条释义里出一道四选一。干扰项来自同语言的其他词条，尽量不重复。</summary>
    public static QuizQuestion? Build(Entry entry, IReadOnlyList<string> distractorGlosses, int optionCount = 4,
        Random? rng = null)
    {
        rng ??= Random.Shared;

        var correct = entry.PrimaryGloss;
        if (string.IsNullOrWhiteSpace(correct))
        {
            return null;
        }

        var options = new List<string> { correct.Trim() };
        foreach (var gloss in distractorGlosses
                     .Select(g => (g ?? string.Empty).Trim())
                     .Where(g => g.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(_ => rng.Next()))
        {
            if (options.Count >= Math.Max(2, optionCount))
            {
                break;
            }

            if (!options.Contains(gloss, StringComparer.OrdinalIgnoreCase))
            {
                options.Add(gloss);
            }
        }

        if (options.Count < 2)
        {
            return null;
        }

        var shuffled = options.OrderBy(_ => rng.Next()).ToList();
        var reading = entry.Readings.FirstOrDefault(r => r.IsCommon)?.Text ?? entry.Readings.FirstOrDefault()?.Text;

        return new QuizQuestion
        {
            Entry = entry,
            Prompt = entry.Term,
            Reading = reading,
            Options = shuffled.Select(o => new QuizOption(o, string.Equals(o, correct.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList(),
        };
    }
}
