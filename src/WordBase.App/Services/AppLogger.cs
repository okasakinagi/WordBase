using System.Text;

namespace WordBase.App.Services;

/// <summary>极简滚动日志。出问题时用户可以把 logs 目录发出来，而不用装调试工具。</summary>
public sealed class AppLogger
{
    private readonly object _gate = new();
    private readonly string _directory;

    public AppLogger(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(message)
                .AppendLine()
                .ToString();

            lock (_gate)
            {
                File.AppendAllText(Path.Combine(_directory, $"wordbase-{DateTime.Now:yyyyMMdd}.log"), line);
            }
        }
        catch (IOException)
        {
            // 日志失败不能影响主流程
        }
    }
}
