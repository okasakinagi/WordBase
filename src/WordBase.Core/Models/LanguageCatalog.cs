namespace WordBase.Core.Models;

public sealed record LanguageOption(string Code, string DisplayName);

public static class LanguageCatalog
{
    public static readonly IReadOnlyList<LanguageOption> BuiltIn = new[]
    {
        new LanguageOption("en", "英语"),
        new LanguageOption("ja", "日语"),
        new LanguageOption("zh", "中文"),
        new LanguageOption("ko", "韩语"),
        new LanguageOption("de", "德语"),
        new LanguageOption("fr", "法语"),
        new LanguageOption("es", "西班牙语"),
    };

    public static string DisplayName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "未指定";
        }

        var match = BuiltIn.FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase));
        return match?.DisplayName ?? code;
    }

    /// <summary>给大模型看的语言全名，例如 ja →「日语（Japanese）」。</summary>
    public static string PromptName(string? code) => code?.ToLowerInvariant() switch
    {
        "en" => "英语（English）",
        "ja" => "日语（Japanese）",
        "zh" => "中文（Chinese）",
        "ko" => "韩语（Korean）",
        "de" => "德语（German）",
        "fr" => "法语（French）",
        "es" => "西班牙语（Spanish）",
        _ => code ?? "未知语言",
    };
}
