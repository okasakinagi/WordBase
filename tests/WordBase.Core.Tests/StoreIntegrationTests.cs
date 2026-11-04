using WordBase.Core.Models;
using WordBase.Core.Scheduling;
using WordBase.Core.Services;
using WordBase.Data;
using Xunit;

namespace WordBase.Core.Tests;

public sealed class TempDatabase : IDisposable
{
    private readonly string _path;

    public TempDatabase()
    {
        _path = Path.Combine(Path.GetTempPath(), $"wordbase-test-{Guid.NewGuid():N}", "wordbase.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Db = new WordBaseDb(_path);
        Db.Initialize();
        Store = new EntryStore(Db);
        Settings = new SettingsStore(Db);
    }

    public WordBaseDb Db { get; }
    public EntryStore Store { get; }
    public SettingsStore Settings { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不影响结论
        }
    }
}

public class StoreIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.FromHours(8));

    private static Entry BuildDraft(string term = "生", string language = "ja", string gloss1 = "生命；生存",
        string gloss2 = "生的；未加工的")
    {
        var entry = new Entry { Term = term, Language = language, Kind = EntryKind.Word, Source = EntrySource.Manual };

        var reading1 = new Reading { Id = -1, Text = "せい", IsCommon = true };
        var reading2 = new Reading { Id = -2, Text = "なま", IsCommon = true };
        entry.Readings.Add(reading1);
        entry.Readings.Add(reading2);

        var sense1 = new Sense { Id = -1, ReadingId = -1, Gloss = gloss1 };
        var sense2 = new Sense { Id = -2, ReadingId = -2, Gloss = gloss2 };
        entry.Senses.Add(sense1);
        entry.Senses.Add(sense2);

        var ordinal = 0;
        foreach (var reading in entry.Readings)
        {
            entry.Examples.Add(new Example
            {
                ReadingId = reading.Id,
                PartOfSpeech = "名词",
                Text = $"{reading.Text} 例句一",
                Translation = "译文一",
                Ordinal = ordinal++,
            });
            entry.Examples.Add(new Example
            {
                ReadingId = reading.Id,
                PartOfSpeech = "名词",
                Text = $"{reading.Text} 例句二",
                Translation = "译文二",
                Ordinal = ordinal++,
            });
        }

        return entry;
    }

    [Fact]
    public void Create_And_Reload_RoundTripsWholeTree()
    {
        using var fixture = new TempDatabase();
        var draft = BuildDraft();

        var id = fixture.Store.Create(draft);
        var loaded = fixture.Store.Get(id);

        Assert.NotNull(loaded);
        Assert.Equal("生", loaded!.Term);
        Assert.Equal("生", loaded.TermNormalized);
        Assert.Equal(2, loaded.Readings.Count);
        Assert.Equal(2, loaded.Senses.Count);
        Assert.Equal(4, loaded.Examples.Count);
        Assert.Equal(CardState.New, loaded.Review.State);
        Assert.NotEqual(0, loaded.Senses[0].ReadingId);
        Assert.Contains(loaded.Examples, e => e.ReadingId == loaded.Readings[1].Id);
    }

    [Fact]
    public void Save_DetectsDuplicateAndRecordsEncounter()
    {
        using var fixture = new TempDatabase();
        var vocabulary = new VocabularyService(fixture.Store, clock: () => Now);

        var first = vocabulary.Save(BuildDraft());
        Assert.Equal(SaveStatus.Saved, first.Status);

        var second = vocabulary.Save(BuildDraft("  生  "));
        Assert.Equal(SaveStatus.Duplicate, second.Status);
        Assert.NotNull(second.Duplicate);
        Assert.Equal(1, second.Duplicate!.HitCount);
    }

    [Fact]
    public void Save_SameTermDifferentLanguage_IsNotDuplicate()
    {
        using var fixture = new TempDatabase();
        var vocabulary = new VocabularyService(fixture.Store, clock: () => Now);

        Assert.Equal(SaveStatus.Saved, vocabulary.Save(BuildDraft("生", "ja")).Status);
        Assert.Equal(SaveStatus.Saved, vocabulary.Save(BuildDraft("生", "zh")).Status);
    }

    [Fact]
    public void Save_InvalidEntry_IsRejectedWithErrors()
    {
        using var fixture = new TempDatabase();
        var vocabulary = new VocabularyService(fixture.Store, clock: () => Now);
        var draft = BuildDraft();
        draft.Senses.Clear();

        var result = vocabulary.Save(draft);
        Assert.Equal(SaveStatus.Invalid, result.Status);
        Assert.True(result.Validation.HasErrors);
    }

    [Fact]
    public void RecordEncounter_PullsDueDateEarlier()
    {
        using var fixture = new TempDatabase();
        var id = fixture.Store.Create(BuildDraft());

        var far = Now.AddDays(100);
        fixture.Store.SaveReview(new ReviewState
        {
            EntryId = id,
            State = CardState.Review,
            DueAt = far,
            Stability = 100,
            Difficulty = 5,
            Reps = 3,
        }, new ReviewLog
        {
            EntryId = id,
            ReviewedAt = Now,
            Phase = "test",
            Rating = Rating.Good,
            PrevState = CardState.Review,
            NextDueAt = far,
        });

        fixture.Store.RecordEncounter(id, DuplicateChannel.Lookup, "生", Now);

        var reloaded = fixture.Store.Get(id)!;
        Assert.Equal(1, reloaded.HitCount);
        Assert.True(reloaded.Review.DueAt < far);
        Assert.True(reloaded.Review.DueAt > Now);
    }

    [Fact]
    public void Update_ReplacesChildrenWithoutLosingReviewState()
    {
        using var fixture = new TempDatabase();
        var id = fixture.Store.Create(BuildDraft());
        var entry = fixture.Store.Get(id)!;
        entry.Examples.RemoveAt(0);
        entry.Readings.RemoveAt(1);
        entry.Senses.RemoveAll(s => s.ReadingId == entry.Readings[0].Id && s.Id != entry.Senses[0].Id);
        fixture.Store.Update(entry);

        var reloaded = fixture.Store.Get(id)!;
        Assert.Single(reloaded.Readings);
        Assert.Equal(3, reloaded.Examples.Count);
        Assert.Equal(CardState.New, reloaded.Review.State);
    }

    [Fact]
    public void Search_FindsByTermGlossAndReading()
    {
        using var fixture = new TempDatabase();
        fixture.Store.Create(BuildDraft());

        Assert.Single(fixture.Store.Search("生", null));
        Assert.Single(fixture.Store.Search("せい", null));
        Assert.Single(fixture.Store.Search("未加工", null));
        Assert.Empty(fixture.Store.Search("不存在", null));
        Assert.Single(fixture.Store.Search(null, "ja"));
        Assert.Empty(fixture.Store.Search(null, "en"));
    }

    [Fact]
    public void DueQueue_And_Stats_ReflectScheduling()
    {
        using var fixture = new TempDatabase();
        var vocabulary = new VocabularyService(fixture.Store, clock: () => Now);
        vocabulary.Save(BuildDraft());
        vocabulary.Save(BuildDraft("走る"));

        var review = new ReviewService(fixture.Store, clock: () => Now);
        Assert.Equal(2, review.CountDue());
        Assert.Equal(2, review.GetDue().Count);

        var entry = review.GetDue()[0];
        review.Submit(entry, Interaction.NotKnown);

        Assert.Equal(1, review.CountDue());
        var stats = review.Stats();
        Assert.Equal(2, stats.Total);
        Assert.Equal(1, stats.Learning);
        Assert.Equal(1, stats.New);
    }

    [Fact]
    public void Submit_ClassifiesInteractions()
    {
        using var fixture = new TempDatabase();
        var vocabulary = new VocabularyService(fixture.Store, clock: () => Now);
        vocabulary.Save(BuildDraft());
        var review = new ReviewService(fixture.Store, clock: () => Now);

        var entry = review.GetDue()[0];
        var outcome = review.Submit(entry, Interaction.KnownAndCorrect);
        Assert.Equal(Rating.Good, outcome.Rating);
        Assert.Equal(1.0, outcome.Confidence);
        Assert.Equal(CardState.Review, outcome.State.State);
        Assert.True(outcome.State.ScheduledDays >= 1);

        var reloaded = fixture.Store.Get(entry.Id)!;
        Assert.Equal(1, reloaded.Review.Reps);
    }

    [Fact]
    public void Distractors_ComeFromOtherEntries()
    {
        using var fixture = new TempDatabase();
        var vocabulary = new VocabularyService(fixture.Store, clock: () => Now);
        vocabulary.Save(BuildDraft());
        vocabulary.Save(BuildDraft("走る", "ja", "跑；奔跑", "离开"));

        var target = fixture.Store.Search("生", null)[0];
        var glosses = fixture.Store.GetDistractorGlosses("ja", target.Id, 10);

        Assert.Contains("跑；奔跑", glosses);
        Assert.DoesNotContain("生命；生存", glosses);
    }

    [Fact]
    public void Settings_RoundTripAndPersist()
    {
        using var fixture = new TempDatabase();
        var settings = new AppSettings(fixture.Settings);
        settings.DefaultLanguage = "ja";
        settings.QuizProbability = 0.4;
        settings.ActiveStart = new TimeOnly(9, 30);

        var reloaded = new AppSettings(new SettingsStore(fixture.Db));
        Assert.Equal("ja", reloaded.DefaultLanguage);
        Assert.Equal(0.4, reloaded.QuizProbability, 3);
        Assert.Equal(new TimeOnly(9, 30), reloaded.ActiveStart);
        Assert.True(reloaded.IsWithinActiveHours(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.FromHours(8))));
        Assert.False(reloaded.IsWithinActiveHours(new DateTimeOffset(2026, 11, 4, 23, 30, 0, TimeSpan.FromHours(8))));
    }
}
