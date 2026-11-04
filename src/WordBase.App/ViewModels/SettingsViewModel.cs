using CommunityToolkit.Mvvm.ComponentModel;
using WordBase.App.Services;
using WordBase.Core.Services;
using WordBase.Llm;

namespace WordBase.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        Load();
    }

    public IReadOnlyList<string> Channels { get; } = new[] { "自动（原生优先，被拦截改自绘）", "仅原生 Windows 通知", "仅自绘卡片" };

    public IReadOnlyList<string> Languages { get; } = new[] { "en", "ja", "zh", "ko", "de", "fr", "es" };

    [ObservableProperty] private string llmBaseUrl = string.Empty;
    [ObservableProperty] private string llmModel = string.Empty;
    [ObservableProperty] private string apiKey = string.Empty;
    [ObservableProperty] private double llmTemperature;
    [ObservableProperty] private double llmTimeoutSeconds;
    [ObservableProperty] private bool llmJsonMode;
    [ObservableProperty] private bool llmEnabled;
    [ObservableProperty] private bool persistApiKey;
    [ObservableProperty] private string llmStatus = string.Empty;

    [ObservableProperty] private double desiredRetentionPercent;
    [ObservableProperty] private double quizProbabilityPercent;
    [ObservableProperty] private double dailyLimit;
    [ObservableProperty] private double maxPerHour;
    [ObservableProperty] private double minBetweenMinutes;
    [ObservableProperty] private string quietStart = "08:00";
    [ObservableProperty] private string quietEnd = "23:00";
    [ObservableProperty] private int channelIndex;

    [ObservableProperty] private bool autostart;
    [ObservableProperty] private bool startMinimized;
    [ObservableProperty] private bool notificationsPaused;
    [ObservableProperty] private string notificationStatus = string.Empty;
    [ObservableProperty] private string dataDirectory = string.Empty;
    [ObservableProperty] private string portableHint = string.Empty;
    [ObservableProperty] private string generalStatus = string.Empty;

    public void Load()
    {
        var settings = _host.Settings;
        LlmBaseUrl = settings.LlmBaseUrl;
        LlmModel = settings.LlmModel;
        ApiKey = _host.ReadApiKey() ?? string.Empty;
        LlmTemperature = settings.LlmTemperature;
        LlmTimeoutSeconds = settings.LlmTimeoutSeconds;
        LlmJsonMode = settings.LlmJsonMode;
        LlmEnabled = settings.LlmEnabled;
        PersistApiKey = settings.PersistApiKey;

        DesiredRetentionPercent = Math.Round(settings.DesiredRetention * 100);
        QuizProbabilityPercent = Math.Round(settings.QuizProbability * 100);
        DailyLimit = settings.DailyLimit;
        MaxPerHour = settings.MaxNotificationsPerHour;
        MinBetweenMinutes = settings.MinMinutesBetweenNotifications;
        QuietStart = settings.ActiveStart.ToString("HH:mm");
        QuietEnd = settings.ActiveEnd.ToString("HH:mm");
        ChannelIndex = (int)settings.Channel;

        Autostart = _host.Autostart.IsEnabled;
        StartMinimized = settings.StartMinimized;
        NotificationsPaused = settings.NotificationsPaused;

        DataDirectory = _host.Paths.DataDirectory;
        PortableHint = _host.Paths.IsPortable
            ? "当前为便携模式：数据库和配置都在程序目录的 data 文件夹里，换机器时整个文件夹拷走即可。建议定期备份。"
            : $"程序目录不可写，已改用 {_host.Paths.DataDirectory}。{_host.Paths.FallbackReason}";

        NotificationStatus = _host.Notifications.Diagnose().Detail;
    }

    public void Save(string? apiKeyInput = null)
    {
        var settings = _host.Settings;
        settings.LlmBaseUrl = LlmBaseUrl;
        settings.LlmModel = LlmModel;
        settings.LlmTemperature = LlmTemperature;
        settings.LlmTimeoutSeconds = (int)LlmTimeoutSeconds;
        settings.LlmJsonMode = LlmJsonMode;
        settings.LlmEnabled = LlmEnabled;
        settings.PersistApiKey = PersistApiKey;

        settings.DesiredRetention = DesiredRetentionPercent / 100.0;
        settings.QuizProbability = QuizProbabilityPercent / 100.0;
        settings.DailyLimit = (int)DailyLimit;
        settings.MaxNotificationsPerHour = (int)MaxPerHour;
        settings.MinMinutesBetweenNotifications = (int)MinBetweenMinutes;

        if (TimeOnly.TryParse(QuietStart, out var start))
        {
            settings.ActiveStart = start;
        }

        if (TimeOnly.TryParse(QuietEnd, out var end))
        {
            settings.ActiveEnd = end;
        }

        settings.Channel = (NotificationChannel)Math.Clamp(ChannelIndex, 0, 2);
        settings.StartMinimized = StartMinimized;
        settings.NotificationsPaused = NotificationsPaused;

        var key = apiKeyInput ?? ApiKey;
        _host.SaveApiKey(string.IsNullOrWhiteSpace(key) ? null : key.Trim());
        ApiKey = key;

        try
        {
            if (Autostart != _host.Autostart.IsEnabled)
            {
                _host.Autostart.SetEnabled(Autostart);
            }

            GeneralStatus = Autostart
                ? "开机自启已开启（注册表 HKCU\\...\\Run，仅当前用户）。"
                : "开机自启已关闭。";
        }
        catch (Exception ex)
        {
            GeneralStatus = "写入开机自启失败：" + ex.Message;
        }

        _host.Scheduler?.Pause(NotificationsPaused);
        _host.Tray?.SetPaused(NotificationsPaused);
        NotificationStatus = _host.Notifications.Diagnose().Detail;
        GeneralStatus = string.IsNullOrEmpty(GeneralStatus) ? "设置已保存。" : GeneralStatus + " 设置已保存。";
    }

    public async Task TestLlmAsync()
    {
        LlmStatus = "正在测试连接…";
        try
        {
            using var client = _host.CreateLlmClient(string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim());
            var result = await client.TestAsync();
            LlmStatus = result.Message;
        }
        catch (Exception ex)
        {
            LlmStatus = "测试失败：" + ex.Message;
        }
    }

    public async Task RunNotificationSelfTestAsync()
    {
        NotificationStatus = "正在发送测试通知…（15 秒内请点通知里的按钮）";
        var result = await _host.Notifications.RunSelfTestAsync(TimeSpan.FromSeconds(15));
        NotificationStatus = result.Summary + Environment.NewLine + result.Diagnostics.Detail;
    }

    public void RefreshDiagnostics() => NotificationStatus = _host.Notifications.Diagnose().Detail;

    public string ExportBackup()
    {
        var path = _host.Backups.Export();
        GeneralStatus = "已导出备份：" + path;
        return path;
    }

    public void ImportBackup(string path)
    {
        var rollback = _host.Backups.Import(path);
        GeneralStatus = $"已导入备份。导入前的数据已存到：{rollback}。建议重启程序以完全生效。";
    }
}
