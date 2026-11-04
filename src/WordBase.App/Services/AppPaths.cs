using System.Text.Json;

namespace WordBase.App.Services;

/// <summary>
/// 便携优先：数据放在 exe 同目录的 data 下；目录不可写时退回 %LOCALAPPDATA%。
/// 每次启动都重新判定，所以把整个文件夹拷到 U 盘也能直接跑。
/// </summary>
public sealed class AppPaths
{
    private AppPaths(string root, bool portable, string? fallbackReason)
    {
        Root = root;
        IsPortable = portable;
        FallbackReason = fallbackReason;
        DataDirectory = root;
        DatabasePath = Path.Combine(root, "wordbase.db");
        ConfigPath = Path.Combine(root, "config.json");
        SecretPath = Path.Combine(root, "secrets.json");
        LogDirectory = Path.Combine(root, "logs");
        BackupDirectory = Path.Combine(root, "backups");
    }

    public string Root { get; }
    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string ConfigPath { get; }
    public string SecretPath { get; }
    public string LogDirectory { get; }
    public string BackupDirectory { get; }
    public bool IsPortable { get; }
    public string? FallbackReason { get; }

    public static AppPaths Create()
    {
        var portableRoot = Path.Combine(AppContext.BaseDirectory, "data");
        if (TryPrepare(portableRoot, out _))
        {
            return new AppPaths(portableRoot, true, null);
        }

        var localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WordBase");
        TryPrepare(localRoot, out var error);
        return new AppPaths(localRoot, false,
            $"程序目录不可写（{portableRoot}），已改用 {localRoot}。原因：{error}");
    }

    private static bool TryPrepare(string path, out string? error)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(BackupDirectory);
    }

    public T? ReadConfig<T>() where T : class
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(ConfigPath));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void WriteConfig<T>(T config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
