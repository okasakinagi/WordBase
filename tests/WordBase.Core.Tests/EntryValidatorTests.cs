using WordBase.Core.Models;
using WordBase.Core.Validation;
using Xunit;

namespace WordBase.Core.Tests;

public class EntryValidatorTests
{
    private static Entry BuildEntry(int readingCount = 1, int examplesPerPos = 2)
    {
        var entry = new Entry { Term = "生", Language = "ja" };

        for (var i = 0; i < readingCount; i++)
        {
            var reading = new Reading { Id = -(i + 1), Text = i == 0 ? "せい" : "なま" };
            entry.Readings.Add(reading);
            entry.Senses.Add(new Sense { Id = -(i + 1), ReadingId = reading.Id, Gloss = "释义" + i });
        }

        var ordinal = 0;
        foreach (var reading in entry.Readings)
        {
            foreach (var pos in new[] { "名词", "动词" })
            {
                for (var n = 0; n < examplesPerPos; n++)
                {
                    entry.Examples.Add(new Example
                    {
                        ReadingId = reading.Id,
                        PartOfSpeech = pos,
                        Text = $"{reading.Text}-{pos}-{n}",
                        Ordinal = ordinal++,
                    });
                }
            }
        }

        return entry;
    }

    [Fact]
    public void CompleteEntry_HasNoIssues()
    {
        var result = new EntryValidator().Validate(BuildEntry());
        Assert.False(result.HasErrors);
        Assert.False(result.HasWarnings);
    }

    [Fact]
    public void MissingTerm_IsError()
    {
        var entry = BuildEntry();
        entry.Term = "  ";
        var result = new EntryValidator().Validate(entry);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Code == "term.required");
    }

    [Fact]
    public void MissingSense_IsError()
    {
        var entry = BuildEntry();
        entry.Senses.Clear();
        var result = new EntryValidator().Validate(entry);
        Assert.Contains(result.Errors, e => e.Code == "sense.required");
    }

    [Fact]
    public void MissingExample_IsError()
    {
        var entry = BuildEntry();
        entry.Examples.Clear();
        var result = new EntryValidator().Validate(entry);
        Assert.Contains(result.Errors, e => e.Code == "example.required");
    }

    [Fact]
    public void FewerExamplesThanTarget_IsWarningOnly()
    {
        var entry = BuildEntry(examplesPerPos: 1);
        var result = new EntryValidator().Validate(entry);

        Assert.False(result.HasErrors);
        Assert.Contains(result.Warnings, w => w.Code == "example.coverage");
    }

    [Fact]
    public void Coverage_ShouldReportPerReadingAndPos()
    {
        var cells = new EntryValidator().Coverage(BuildEntry(readingCount: 2));
        Assert.Equal(4, cells.Count);
        Assert.All(cells, c => Assert.True(c.Ok));
    }

    [Fact]
    public void UnverifiedContent_IsWarning()
    {
        var entry = BuildEntry();
        entry.Examples[0].IsVerified = false;
        var result = new EntryValidator().Validate(entry);
        Assert.Contains(result.Warnings, w => w.Code == "content.unverified");
    }

    [Fact]
    public void EntryWithoutReadings_FallsBackToPosBuckets()
    {
        var entry = new Entry { Term = "run" , Language = "en" };
        entry.Senses.Add(new Sense { Gloss = "跑" });
        entry.Examples.Add(new Example { PartOfSpeech = "verb", Text = "I run.", ReadingId = null });
        entry.Examples.Add(new Example { PartOfSpeech = "verb", Text = "He runs.", ReadingId = null });

        var result = new EntryValidator().Validate(entry);
        Assert.False(result.HasWarnings);
    }
}
