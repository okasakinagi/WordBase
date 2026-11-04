using WordBase.Core.Models;
using WordBase.Core.Quizzing;
using Xunit;

namespace WordBase.Core.Tests;

public class QuizBuilderTests
{
    private static Entry Target()
    {
        var entry = new Entry { Id = 1, Term = "生", Language = "ja" };
        entry.Readings.Add(new Reading { Id = -1, Text = "せい", IsCommon = true });
        entry.Senses.Add(new Sense { Gloss = "生命；生存" });
        return entry;
    }

    [Fact]
    public void Build_CreatesOptionsWithExactlyOneCorrect()
    {
        var question = QuizBuilder.Build(Target(), new[] { "跑", "吃", "睡觉" }, 4, new Random(1));

        Assert.NotNull(question);
        Assert.Equal(4, question!.Options.Count);
        Assert.Single(question.Options, o => o.IsCorrect);
        Assert.Equal("生命；生存", question.Options[question.CorrectIndex].Text);
        Assert.Equal("せい", question.Reading);
    }

    [Fact]
    public void Build_WithoutDistractors_IsImpossible()
    {
        Assert.Null(QuizBuilder.Build(Target(), Array.Empty<string>(), 4, new Random(1)));
    }

    [Fact]
    public void Build_IgnoresDuplicateDistractors()
    {
        var question = QuizBuilder.Build(Target(), new[] { "生命；生存", "生命；生存", "跑" }, 4, new Random(2));
        Assert.NotNull(question);
        Assert.Equal(2, question!.Options.Count);
        Assert.Single(question.Options, o => o.IsCorrect);
    }

    [Fact]
    public void Build_ReturnsNullWhenSenseIsEmpty()
    {
        var entry = new Entry { Id = 2, Term = "x" };
        Assert.Null(QuizBuilder.Build(entry, new[] { "a" }));
    }
}

