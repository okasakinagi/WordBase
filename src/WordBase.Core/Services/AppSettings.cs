using System.Globalization;
using WordBase.Core.Abstractions;

namespace WordBase.Core.Services;

public enum NotificationChannel
{
    /// <summary>先试原生通知，检测到被拦截时自动改自绘卡片。</summary>
    Auto = 0,
    Toast = 1,
    Popup = 2,
}

/// <summary>强类型设置门面：对上层只暴露语义化属性，底层仍是一张 key/value 表，便于增量加字段。</summary>
public sealed class AppSettings
{
    private readonly ISettingsStore _store;
    private readonly Dictionary<string, string> _cache;

    public AppSettings(ISettingsStore store)
    {
        _store = store;
        _cache = new Dictionary<string, string>(store.GetAll(), StringComparer.Ordinal);
    }

    public string DefaultLanguage
    {
        get => GetString("app.defaultLanguage", "en");
        set => Put("app.defaultLanguage", value);
    }

    public bool Autostart
    {
        get => GetBool("app.autostart", false);
        set => Put("app.autostart", value);
    }

    public bool StartMinimized
    {
        get => GetBool("app.startMinimized", false);
        set => Put("app.startMinimized", value);
    }

    public int ExamplesPerReadingPos
    {
        get => GetInt("learning.examplesPerReadingPos", 2);
        set => Put("learning.examplesPerReadingPos", Math.Clamp(value, 1, 10));
    }

    public double DesiredRetention
    {
        get => GetDouble("review.desiredRetention", 0.9);
        set => Put("review.desiredRetention", Math.Clamp(value, 0.7, 0.98));
    }

    /// <summary>「认识」之后仍然抽一道选择题的概率。</summary>
    public double QuizProbability
    {
        get => GetDouble("review.quizProbability", 0.25);
        set => Put("review.quizProbability", Math.Clamp(value, 0, 1));
    }

    public int DailyLimit
    {
        get => GetInt("review.dailyLimit", 60);
        set => Put("review.dailyLimit", Math.Clamp(value, 1, 1000));
    }

    public int MaxNotificationsPerHour
    {
        get => GetInt("review.maxNotificationsPerHour", 6);
        set => Put("review.maxNotificationsPerHour", Math.Clamp(value, 1, 120));
    }

    public int MinMinutesBetweenNotifications
    {
        get => GetInt("review.minMinutesBetweenNotifications", 15);
        set => Put("review.minMinutesBetweenNotifications", Math.Clamp(value, 1, 600));
    }

    /// <summary>允许提醒的时间窗起点（默认 08:00）。窗口之外不打扰。</summary>
    public TimeOnly ActiveStart
    {
        get => GetTime("review.activeStart", new TimeOnly(8, 0));
        set => Put("review.activeStart", value.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    /// <summary>允许提醒的时间窗终点（默认 23:00）。</summary>
    public TimeOnly ActiveEnd
    {
        get => GetTime("review.activeEnd", new TimeOnly(23, 0));
        set => Put("review.activeEnd", value.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    public NotificationChannel Channel
    {
        get => GetEnum("notify.channel", NotificationChannel.Auto);
        set => Put("notify.channel", value.ToString());
    }

    public bool NotificationsPaused
    {
        get => GetBool("notify.paused", false);
        set => Put("notify.paused", value);
    }

    public string LlmBaseUrl
    {
        get => GetString("llm.baseUrl", "https://api.openai.com/v1");
        set => Put("llm.baseUrl", value.Trim());
    }

    public string LlmModel
    {
        get => GetString("llm.model", "gpt-4o-mini");
        set => Put("llm.model", value.Trim());
    }

    public double LlmTemperature
    {
        get => GetDouble("llm.temperature", 0.2);
        set => Put("llm.temperature", Math.Clamp(value, 0, 2));
    }

    public int LlmTimeoutSeconds
    {
        get => GetInt("llm.timeoutSeconds", 60);
        set => Put("llm.timeoutSeconds", Math.Clamp(value, 10, 600));
    }

    public bool LlmJsonMode
    {
        get => GetBool("llm.jsonMode", true);
        set => Put("llm.jsonMode", value);
    }

    public bool LlmEnabled
    {
        get => GetBool("llm.enabled", true);
        set => Put("llm.enabled", value);
    }

    /// <summary>是否保存 API Key。关掉时每次启动都要重新输入，适合公用机器。</summary>
    public bool PersistApiKey
    {
        get => GetBool("llm.persistKey", true);
        set => Put("llm.persistKey", value);
    }

    public string? Raw(string key) => _cache.TryGetValue(key, out var v) ? v : null;

    /// <summary>当前是否在允许提醒的时间窗内（支持跨零点，例如 22:00–02:00）。</summary>
    public bool IsWithinActiveHours(DateTimeOffset now)
    {
        var start = ActiveStart;
        var end = ActiveEnd;
        var current = TimeOnly.FromDateTime(now.LocalDateTime);

        if (start == end)
        {
            return true;
        }

        return start < end
            ? current >= start && current < end
            : current >= start || current < end;
    }

    private string GetString(string key, string fallback) =>
        _cache.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private int GetInt(string key, int fallback) =>
        _cache.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private double GetDouble(string key, double fallback) =>
        _cache.TryGetValue(key, out var value) &&
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private bool GetBool(string key, bool fallback) =>
        _cache.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private TimeOnly GetTime(string key, TimeOnly fallback) =>
        _cache.TryGetValue(key, out var value) && TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;

    private TEnum GetEnum<TEnum>(string key, TEnum fallback) where TEnum : struct, Enum =>
        _cache.TryGetValue(key, out var value) && Enum.TryParse<TEnum>(value, true, out var parsed) ? parsed : fallback;

    private void Put(string key, string value)
    {
        _cache[key] = value;
        _store.Set(key, value);
    }

    private void Put(string key, bool value) => Put(key, value ? "true" : "false");

    private void Put(string key, int value) => Put(key, value.ToString(CultureInfo.InvariantCulture));

    private void Put(string key, double value) => Put(key, value.ToString("0.####", CultureInfo.InvariantCulture));
}
