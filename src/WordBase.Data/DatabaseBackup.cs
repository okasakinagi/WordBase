using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace WordBase.Data;

/// <summary>备份包的概况，供导入前确认用。EntryCount = -1 表示读不出来（结构太旧）。</summary>
public sealed record BackupInfo(bool HasDatabase, int EntryCount);

/// <summary>
/// 数据目录的快照与恢复。只打包数据目录**顶层**的文件，绝不把 backups / logs 子目录卷进来 —
/// 之前把回滚包写进 data\backups 又去打包整个 data\，于是 zip 把自己装进去，报 "file is being used"。
/// </summary>
public sealed class DatabaseBackup
{
    /// <summary>目录里需要进备份的文件。密钥是 DPAPI 密文，换机器解不开，留着不影响安全。</summary>
    private static readonly string[] IncludedNames = { "wordbase.db", "config.json", "secrets.json" };

    private readonly string _dataDirectory;
    private readonly string _backupDirectory;
    private readonly WordBaseDb _db;
    private readonly Action<string>? _log;

    public DatabaseBackup(string dataDirectory, string backupDirectory, WordBaseDb db, Action<string>? log = null)
    {
        _dataDirectory = dataDirectory;
        _backupDirectory = backupDirectory;
        _db = db;
        _log = log;
    }

    public string DataDirectory => _dataDirectory;
    public string BackupDirectory => _backupDirectory;

    /// <summary>
    /// 读取备份包里的词条数，用于导入前确认。用户很容易误选到「回滚包」，
    /// 而失败导入留下的回滚包可能是空库 —— 静默覆盖就等于丢数据。
    /// </summary>
    public BackupInfo Inspect(string zipPath)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("备份文件不存在", zipPath);
        }

        var staging = Path.Combine(Path.GetTempPath(), $"wordbase-inspect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            var extracted = Path.Combine(staging, "inspect.db");
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.GetEntry("wordbase.db");
                if (entry is null)
                {
                    return new BackupInfo(false, 0);
                }

                entry.ExtractToFile(extracted, overwrite: true);
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = extracted,
                Mode = SqliteOpenMode.ReadOnly,
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM entry;";
            return new BackupInfo(true, Convert.ToInt32(command.ExecuteScalar()));
        }
        catch (SqliteException)
        {
            // 表结构对不上（很旧的备份），交给正常流程去迁移
            return new BackupInfo(true, -1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(staging);
        }
    }

    public int CountCurrentEntries()
    {
        try
        {
            using var connection = _db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM entry;";
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    public string Export()
    {
        Directory.CreateDirectory(_backupDirectory);
        var target = Path.Combine(_backupDirectory, $"wordbase-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        WriteSnapshot(target);
        _log?.Invoke($"已导出备份：{target}");
        return target;
    }

    /// <summary>
    /// 用备份覆盖当前数据。先把当前状态打包成回滚包（写到临时目录再搬进 backups，
    /// 避免边写边被打包），返回回滚包路径。
    /// </summary>
    public string Import(string zipPath)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("备份文件不存在", zipPath);
        }

        var staging = Path.Combine(Path.GetTempPath(), $"wordbase-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
            var stagedDb = Path.Combine(staging, "wordbase.db");
            if (!File.Exists(stagedDb))
            {
                throw new InvalidDataException("压缩包里没有 wordbase.db，不像是 WordBase 的备份。");
            }

            var rollback = Path.Combine(_backupDirectory, $"pre-import-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            Directory.CreateDirectory(_backupDirectory);

            // 关键：先写到数据目录之外，再搬进来；否则归档时会把正在写的 zip 自己读进去
            var tempRollback = Path.Combine(staging, "rollback.zip");
            WriteSnapshot(tempRollback);
            File.Move(tempRollback, rollback, overwrite: true);

            CopyIntoDataDirectory(staging, "wordbase.db", stagedDb);
            CopyIntoDataDirectory(staging, "config.json", Path.Combine(staging, "config.json"));
            CopyIntoDataDirectory(staging, "secrets.json", Path.Combine(staging, "secrets.json"));

            _log?.Invoke($"已导入备份 {zipPath}，回滚包：{rollback}");
            return rollback;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// 把数据目录顶层文件打包。先 checkpoint 把 WAL 折进主库，这样只备份 wordbase.db 也完整，
    /// 且不会因为 -wal/-shm 正在被占用而失败。
    /// </summary>
    private void WriteSnapshot(string destinationZip)
    {
        Checkpoint();
        SqliteConnection.ClearAllPools();

        using var archive = ZipFile.Open(destinationZip, ZipArchiveMode.Create);
        foreach (var name in IncludedNames)
        {
            var path = Path.Combine(_dataDirectory, name);
            if (File.Exists(path))
            {
                archive.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
            }
        }
    }

    private void CopyIntoDataDirectory(string staging, string name, string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var destination = Path.Combine(_dataDirectory, name);

        // 数据库要先清掉旧的 -wal/-shm，否则残留的日志会和换进来的主库对不上
        if (name == "wordbase.db")
        {
            DeleteSidecar(destination + "-wal");
            DeleteSidecar(destination + "-shm");
        }

        var temp = destination + ".importing";
        File.Copy(sourcePath, temp, overwrite: true);
        File.Move(temp, destination, overwrite: true);
    }

    private void Checkpoint()
    {
        try
        {
            using var connection = _db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"备份前 checkpoint 失败（不影响继续）：{ex.Message}");
        }
    }

    private void DeleteSidecar(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"清理 {Path.GetFileName(path)} 失败：{ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结果，交给系统清理
        }
    }
}
