using System.IO.Compression;
using WordBase.Core.Models;
using WordBase.Core.Services;
using WordBase.Data;
using Xunit;

namespace WordBase.Core.Tests;

/// <summary>
/// 备份/恢复回归测试。重点是 Import 不能在数据目录里一边写回滚包一边打包整个目录
/// （那会报 "The process cannot access the file ... because it is being used by another process"）。
/// </summary>
public sealed class BackupFixture : IDisposable
{
    private readonly string _root;

    public BackupFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wordbase-backup-test-{Guid.NewGuid():N}");
        DataDirectory = Path.Combine(_root, "data");
        BackupDirectory = Path.Combine(DataDirectory, "backups");
        Directory.CreateDirectory(BackupDirectory);

        Db = new WordBaseDb(Path.Combine(DataDirectory, "wordbase.db"));
        Db.Initialize();
        Store = new EntryStore(Db);
        Backups = new DatabaseBackup(DataDirectory, BackupDirectory, Db);
    }

    public string DataDirectory { get; }
    public string BackupDirectory { get; }
    public WordBaseDb Db { get; }
    public EntryStore Store { get; }
    public DatabaseBackup Backups { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响结论
        }
    }
}

public class DatabaseBackupTests
{
    private static Entry BuildEntry(string term)
    {
        var entry = new Entry { Term = term, Language = "ja" };
        entry.Readings.Add(new Reading { Id = -1, Text = "よみ", IsCommon = true });
        entry.Senses.Add(new Sense { Id = -1, ReadingId = -1, Gloss = "释义-" + term });
        entry.Examples.Add(new Example { ReadingId = -1, PartOfSpeech = "名词", Text = "例文-" + term });
        entry.Examples.Add(new Example { ReadingId = -1, PartOfSpeech = "名词", Text = "例文2-" + term });
        return entry;
    }

    [Fact]
    public void Export_ContainsDatabaseAndConfig_ButNoNestedBackups()
    {
        using var fixture = new BackupFixture();
        fixture.Store.Create(BuildEntry("生"));
        File.WriteAllText(Path.Combine(fixture.DataDirectory, "config.json"), "{}");

        // 先放一个旧备份，确认不会被打进新备份
        File.WriteAllText(Path.Combine(fixture.BackupDirectory, "old.zip"), "not really a zip");

        var zip = fixture.Backups.Export();

        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("wordbase.db", names);
        Assert.Contains("config.json", names);
        Assert.DoesNotContain(names, n => n.StartsWith("backups/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.EndsWith("-wal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Export_ThenImport_RestoresPreviousState()
    {
        using var fixture = new BackupFixture();
        var vocabulary = new VocabularyService(fixture.Store);
        vocabulary.Save(BuildEntry("生"));

        var zip = fixture.Backups.Export();

        // 导出后把库改乱：删掉「生」，加一条「走る」
        var saved = fixture.Store.Search("生", null).Single();
        fixture.Store.Delete(saved.Id);
        vocabulary.Save(BuildEntry("走る"));
        Assert.Single(fixture.Store.Search("走る", null));

        fixture.Backups.Import(zip);

        var reopened = new EntryStore(fixture.Db);
        Assert.Single(reopened.Search("生", null));
        Assert.Empty(reopened.Search("走る", null));
    }

    /// <summary>这条就是用户遇到的崩溃：导入时回滚包写在 backups 里，而打包又把 backups 卷进去。</summary>
    [Fact]
    public void Import_DoesNotThrowWhenRollbackLandsInsideDataDirectory()
    {
        using var fixture = new BackupFixture();
        var vocabulary = new VocabularyService(fixture.Store);
        vocabulary.Save(BuildEntry("生"));

        var zip = fixture.Backups.Export();
        vocabulary.Save(BuildEntry("走る"));

        // 不应抛 IOException / UnauthorizedAccessException
        var rollback = fixture.Backups.Import(zip);

        Assert.True(File.Exists(rollback));
        Assert.Equal(fixture.BackupDirectory, Path.GetDirectoryName(rollback));
    }

    [Fact]
    public void Import_RollbackArchiveIsReadableAndContainsTheStateBeingReplaced()
    {
        using var fixture = new BackupFixture();
        var vocabulary = new VocabularyService(fixture.Store);
        vocabulary.Save(BuildEntry("生"));
        var zip = fixture.Backups.Export();

        vocabulary.Save(BuildEntry("走る"));
        var rollback = fixture.Backups.Import(zip);

        using var archive = ZipFile.OpenRead(rollback);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("wordbase.db", names);
        Assert.DoesNotContain(names, n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Import_RejectsArchiveWithoutDatabase()
    {
        using var fixture = new BackupFixture();
        var bogus = Path.Combine(Path.GetTempPath(), $"wordbase-bogus-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(bogus, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("not a backup");
        }

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => fixture.Backups.Import(bogus));
            Assert.Contains("wordbase.db", ex.Message);
        }
        finally
        {
            File.Delete(bogus);
        }
    }

    [Fact]
    public void Import_MissingFile_Throws()
    {
        using var fixture = new BackupFixture();
        Assert.Throws<FileNotFoundException>(() =>
            fixture.Backups.Import(Path.Combine(Path.GetTempPath(), "definitely-not-here.zip")));
    }

    [Fact]
    public void Import_KeepsBackupsDirectoryIntact()
    {
        using var fixture = new BackupFixture();
        fixture.Store.Create(BuildEntry("生"));
        var zip = fixture.Backups.Export();
        var preexisting = Path.Combine(fixture.BackupDirectory, "old.zip");
        File.WriteAllText(preexisting, "keep me");

        fixture.Backups.Import(zip);

        Assert.True(File.Exists(preexisting));
        Assert.True(File.Exists(zip));
    }
}
