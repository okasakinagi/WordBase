using Microsoft.UI.Dispatching;
using WordBase.App.Services;
using WordBase.Core.Abstractions;
using WordBase.Core.Scheduling;
using WordBase.Core.Services;
using WordBase.Data;
using WordBase.Llm;

namespace WordBase.App;

/// <summary>组合根：所有服务在这里装配一次，页面只拿实例，不自己 new 依赖。</summary>
public sealed class AppHost : IDisposable
{
    private AppHost(AppPaths paths, AppLogger logger, WordBaseDb db)
    {
        Paths = paths;
        Logger = logger;
        Database = db;
        Entries = new EntryStore(db);
        SettingsStore = new SettingsStore(db);
        Settings = new AppSettings(SettingsStore);
        Secrets = new DpapiSecretStore(paths.SecretPath);
        Vocabulary = new VocabularyService(Entries);
        Review = new ReviewService(Entries, BuildScheduler(), clock: () => DateTimeOffset.Now);
        Autostart = new AutostartService(logger);
        Notifications = new NotificationService(logger, Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        Backups = new BackupService(paths, logger);
    }

    public AppPaths Paths { get; }
    public AppLogger Logger { get; }
    public WordBaseDb Database { get; }
    public EntryStore Entries { get; }
    public ISettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public DpapiSecretStore Secrets { get; }
    public VocabularyService Vocabulary { get; }
    public ReviewService Review { get; }
    public AutostartService Autostart { get; }
    public NotificationService Notifications { get; }
    public BackupService Backups { get; }
    public TrayIconService? Tray { get; set; }
    public NotificationScheduler? Scheduler { get; private set; }

    public static AppHost Create()
    {
        var paths = AppPaths.Create();
        paths.EnsureDirectories();
        var logger = new AppLogger(paths.LogDirectory);
        logger.Info($"启动，便携模式={paths.IsPortable}，数据目录={paths.DataDirectory}");

        var db = new WordBaseDb(paths.DatabasePath);
        db.Initialize();
        return new AppHost(paths, logger, db);
    }

    public void AttachUiServices(DispatcherQueue dispatcher, TrayIconService tray)
    {
        Tray = tray;
        Scheduler = new NotificationScheduler(Review, Notifications, Settings, Logger, dispatcher);
        Scheduler.Pause(Settings.NotificationsPaused);
    }

    private ReviewScheduler BuildScheduler() => new(new SchedulerOptions
    {
        DesiredRetention = Settings.DesiredRetention,
    });

    public string? ReadApiKey() => Secrets.Read("llm.apiKey");

    public void SaveApiKey(string? key)
    {
        if (Settings.PersistApiKey)
        {
            Secrets.Write("llm.apiKey", key);
        }
        else
        {
            Secrets.Write("llm.apiKey", null);
        }
    }

    public LlmOptions BuildLlmOptions(string? apiKeyOverride = null) => new()
    {
        BaseUrl = Settings.LlmBaseUrl,
        Model = Settings.LlmModel,
        ApiKey = apiKeyOverride ?? ReadApiKey() ?? string.Empty,
        Temperature = Settings.LlmTemperature,
        TimeoutSeconds = Settings.LlmTimeoutSeconds,
        UseJsonMode = Settings.LlmJsonMode,
    };

    public ILlmClient CreateLlmClient(string? apiKeyOverride = null) =>
        new OpenAiCompatibleClient(BuildLlmOptions(apiKeyOverride));

    public void Dispose()
    {
        Scheduler?.Dispose();
        Notifications.Dispose();
        Tray?.Dispose();
    }
}
