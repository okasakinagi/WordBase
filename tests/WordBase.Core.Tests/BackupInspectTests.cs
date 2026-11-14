using System.IO.Compression;
using WordBase.Core.Models;
using WordBase.Core.Services;
using WordBase.Data;
using Xunit;

namespace WordBase.Core.Tests;

public class BackupInspectTests
{
    private static Entry BuildEntry(string term)
    {
        var entry = new Entry { Term = term, Language = "ja" };
        entry.Readings.Add(new Reading { Id = -1, Text = "よみ", IsCommon = true });
        entry.Senses.Add(new Sense { Id = -1, ReadingId = -1, Gloss = "释义" });
        entry.Examples.Add(new Example { ReadingId = -1, PartOfSpeech = "名词", Text = "例文" });
        entry.Examples.Add(new Example { ReadingId = -1, PartOfSpeech = "名词", Text = "例文2" });
        return entry;
    }

    [Fact]
    public void Inspect_CountsEntriesInBackup()
    {
        using var fixture = new BackupFixture();
        var vocabulary = new VocabularyService(fixture.Store);
        vocabulary.Save(BuildEntry("生"));
        vocabulary.Save(BuildEntry("走る"));

        var zip = fixture.Backups.Export();
        var info = fixture.Backups.Inspect(zip);

        Assert.True(info.HasDatabase);
        Assert.Equal(2, info.EntryCount);
    }

    [Fact]
    public void Inspect_EmptyDatabase_ReportsZero()
    {
        using var fixture = new BackupFixture();
        var zip = fixture.Backups.Export();

        var info = fixture.Backups.Inspect(zip);

        Assert.True(info.HasDatabase);
        Assert.Equal(0, info.EntryCount);
    }

    [Fact]
    public void Inspect_ArchiveWithoutDatabase_ReportsMissing()
    {
        using var fixture = new BackupFixture();
        var bogus = Path.Combine(Path.GetTempPath(), $"wordbase-inspect-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(bogus, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("nope");
        }

        try
        {
            var info = fixture.Backups.Inspect(bogus);
            Assert.False(info.HasDatabase);
        }
        finally
        {
            File.Delete(bogus);
        }
    }

    [Fact]
    public void CountCurrentEntries_ReflectsLiveData()
    {
        using var fixture = new BackupFixture();
        Assert.Equal(0, fixture.Backups.CountCurrentEntries());

        new VocabularyService(fixture.Store).Save(BuildEntry("生"));
        Assert.Equal(1, fixture.Backups.CountCurrentEntries());
    }

    /// <summary>
    /// 用户实际踩到的坑：导入失败留下的回滚包是空库，再导入它会清空词库。
    /// 这里确认 Inspect 能识别出"空库"，界面据此拦截。
    /// </summary>
    [Fact]
    public void EmptyRollback_IsDetectableBeforeItWipesData()
    {
        using var fixture = new BackupFixture();
        var vocabulary = new VocabularyService(fixture.Store);

        var empty = fixture.Backups.Export();      // 空库导出
        vocabulary.Save(BuildEntry("生"));          // 之后才有数据

        var info = fixture.Backups.Inspect(empty);
        Assert.Equal(0, info.EntryCount);
        Assert.Equal(1, fixture.Backups.CountCurrentEntries());
    }
}
