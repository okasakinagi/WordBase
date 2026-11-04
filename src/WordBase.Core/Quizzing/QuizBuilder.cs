using WordBase.Core.Models;

namespace WordBase.Core.Quizzing;

public sealed record QuizOption(string Text, bool IsCorrect, long? SourceEntryId = null);

public sealed record Distractor(string Gloss, long EntryId);

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
        Random? rng = null) =>
        Build(entry, distractorGlosses.Select(g => new Distractor(g, 0)).ToList(), optionCount, rng);

    public static QuizQuestion? Build(Entry entry, IReadOnlyList<Distractor> distractors, int optionCount = 4,
        Random? rng = null)
    {
        rng ??= Random.Shared;

        var correct = entry.PrimaryGloss;
        if (string.IsNullOrWhiteSpace(correct))
        {
            return null;
        }

        var options = new List<Distractor> { new(correct.Trim(), entry.Id) };
        foreach (var distractor in distractors
                     .Where(d => !string.IsNullOrWhiteSpace(d.Gloss))
                     .DistinctBy(d => d.Gloss.Trim(), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(_ => rng.Next()))
        {
            if (options.Count >= Math.Max(2, optionCount))
            {
                break;
            }

            var text = distractor.Gloss.Trim();
            if (!options.Any(o => string.Equals(o.Gloss, text, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new Distractor(text, distractor.EntryId));
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
            Options = shuffled
                .Select(o => new QuizOption(o.Gloss, string.Equals(o.Gloss, correct.Trim(), StringComparison.OrdinalIgnoreCase),
                    o.EntryId == entry.Id ? null : o.EntryId))
                .ToList(),
        };
    }
}
