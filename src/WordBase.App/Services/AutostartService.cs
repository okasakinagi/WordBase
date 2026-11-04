using Microsoft.Win32;

namespace WordBase.App.Services;

/// <summary>
/// 开机自启走 HKCU\...\Run，不需要管理员权限。
/// 便携模式下的坑：移动目录后注册表里的路径会失效，所以每次启动都做一次路径校正。
/// </summary>
public sealed class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WordBase";
    private readonly AppLogger _logger;

    public AutostartService(AppLogger logger) => _logger = logger;

    public string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WordBase.exe");

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex)
            {
                _logger.Warn($"读取自启项失败：{ex.Message}");
                return false;
            }
        }
    }

    public string? RegisteredCommand
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) as string;
        }
    }

    public bool IsRemovableDrive()
    {
        try
        {
            var root = Path.GetPathRoot(ExecutablePath);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            return new DriveInfo(root).DriveType is DriveType.Removable or DriveType.Network;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{ExecutablePath}\" --minimized");
                _logger.Info($"已注册开机自启：{ExecutablePath}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.Info("已取消开机自启");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("写入开机自启失败", ex);
            throw;
        }
    }

    /// <summary>启动时校正：注册了但路径不是当前 exe（移动过目录），就地修复或移除。</summary>
    public void Reconcile()
    {
        if (!IsEnabled)
        {
            return;
        }

        var expected = $"\"{ExecutablePath}\" --minimized";
        if (string.Equals(RegisteredCommand, expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsRemovableDrive())
        {
            // 可移动盘开机时可能还没挂载，留着只会弹错误框
            SetEnabled(false);
            _logger.Warn("检测到程序位于可移动磁盘，已自动关闭开机自启");
            return;
        }

        SetEnabled(true);
        _logger.Info("检测到自启路径漂移，已更新为新路径");
    }
}
