using System.Text;
using System.Text.RegularExpressions;

namespace WordBase.Core.Text;

/// <summary>
/// 查重用的词形规范化。只做「不改变语义」的处理：
/// NFKC（全角→半角、半角假名→全角假名）、空白折叠、大小写折叠。
/// 刻意不删标点，避免把 (笑)、(one's) 之类有意义的符号吃掉。
/// </summary>
public static partial class TermNormalizer
{
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Normalize(NormalizationForm.FormKC);
        text = WhitespaceRegex().Replace(text, " ");
        return text.Trim().ToLowerInvariant();
    }

    /// <summary>展示用：仅去首尾空白并折叠内部空白，保留原始大小写与全半角。</summary>
    public static string Tidy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return WhitespaceRegex().Replace(raw, " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
