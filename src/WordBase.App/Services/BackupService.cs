using System.IO.Compression;

namespace WordBase.App.Services;

/// <summary>
/// 便携模式下数据就在程序目录，用户很可能忘记备份。这里提供一键导出/导入。
/// 备份包里包含数据库、设置和加密后的密钥（密钥只能在本机本用户下解开）。
/// </summary>
public sealed class BackupService
{
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;

    public BackupService(AppPaths paths, AppLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public string Export()
    {
        Directory.CreateDirectory(_paths.BackupDirectory);
        var target = Path.Combine(_paths.BackupDirectory, $"wordbase-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        // 先把 WAL 落盘，避免备份到一半的数据
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var archive = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(_paths.DataDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name == ".write-probe")
            {
                continue;
            }

            archive.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
        }

        _logger.Info($"已导出备份：{target}");
        return target;
    }

    /// <summary>导入前会把当前数据复制成一份 pre-import 备份，失败可回滚。</summary>
    public string Import(string zipPath)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("备份文件不存在", zipPath);
        }

        var staging = Path.Combine(Path.GetTempPath(), $"wordbase-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

        if (!File.Exists(Path.Combine(staging, "wordbase.db")))
        {
            Directory.Delete(staging, recursive: true);
            throw new InvalidDataException("压缩包里没有 wordbase.db，不像是 WordBase 的备份。");
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var rollback = Path.Combine(_paths.BackupDirectory, $"pre-import-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        Directory.CreateDirectory(_paths.BackupDirectory);
        ZipFile.CreateFromDirectory(_paths.DataDirectory, rollback, CompressionLevel.Optimal, includeBaseDirectory: false);

        foreach (var file in Directory.EnumerateFiles(staging))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("wordbase.db", StringComparison.OrdinalIgnoreCase) || name == "config.json" ||
                name == "secrets.json")
            {
                File.Copy(file, Path.Combine(_paths.DataDirectory, name), overwrite: true);
            }
        }

        Directory.Delete(staging, recursive: true);
        _logger.Info($"已导入备份 {zipPath}，回滚包：{rollback}");
        return rollback;
    }
}
