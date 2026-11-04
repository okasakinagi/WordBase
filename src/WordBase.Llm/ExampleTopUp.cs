using System.Text;
using System.Text.Json;

namespace WordBase.Llm;

/// <summary>某个「读音 × 词性」还差几条例句。</summary>
public sealed record ExampleGap(string Reading, string PartOfSpeech, int Have, int Need);

public sealed record ExampleAddition(string Reading, string PartOfSpeech, string Text, string? Translation, string? Note);

public sealed record TopUpParseResult(IReadOnlyList<ExampleAddition> Additions, string? Error)
{
    public bool Ok => Additions.Count > 0;
}

/// <summary>
/// 第二轮请求：只补例句。
/// 第一轮模型有时凑不满「每个读音×词性 2 条」，这里把缺口单独再要一次，
/// 避免为了补两条例句而让模型重排整份内容（那样容易顺手改坏已有的读音和释义）。
/// </summary>
public static class ExampleTopUpPromptBuilder
{
    public static string SystemPrompt()
    {
        var buffer = new StringBuilder();
        buffer.AppendLine("你是 WordBase 的例句补充助手。用户已有一条词条，但某些「读音 × 词性」的例句数量不足。");
        buffer.AppendLine("你的唯一任务：只补缺少的例句，输出 JSON。不要重写释义、不要增删读音、不要改词条本身。");
        buffer.AppendLine();
        buffer.AppendLine("硬性要求：");
        buffer.AppendLine("1. 只输出一个 JSON 对象，不要解释，不要 Markdown 代码块。");
        buffer.AppendLine("2. 每条例句必须真的用到指定的词条和指定读音，并且体现指定的词性。");
        buffer.AppendLine("3. 例句必须语法正确、语义自然、母语者真的会这么说；不同例句的句式要尽量不同，不要把同一句改几个词充数。");
        buffer.AppendLine("4. 只补用户点名的那几个组合，不要多给，不要给别的词性或别的读音。");
        buffer.AppendLine("5. 如果某个组合你确实写不出自然的例句，就把它从 additions 里省略，并在该条目的 note 里说明原因；宁可少给，绝不编造。");
        buffer.AppendLine();
        buffer.AppendLine("输出 schema：");
        buffer.AppendLine("{");
        buffer.AppendLine("  \"additions\": [");
        buffer.AppendLine("    {");
        buffer.AppendLine("      \"reading\": \"读音原文（与用户给的写法一致；无读音则为空字符串）\",");
        buffer.AppendLine("      \"pos\": \"词性\",");
        buffer.AppendLine("      \"examples\": [");
        buffer.AppendLine("        { \"text\": \"例句原文\", \"translation\": \"中文翻译\", \"note\": \"补充说明或 null\" }");
        buffer.AppendLine("      ]");
        buffer.AppendLine("    }");
        buffer.AppendLine("  ]");
        buffer.AppendLine("}");
        return buffer.ToString();
    }

    public static string UserPrompt(string term, string language, IReadOnlyList<ExampleGap> gaps,
        IReadOnlyDictionary<string, string[]>? glossesByReading = null)
    {
        var buffer = new StringBuilder();
        buffer.AppendLine($"词条：{term}");
        buffer.AppendLine($"语言：{Core.Models.LanguageCatalog.PromptName(language)}");
        buffer.AppendLine();

        if (glossesByReading is { Count: > 0 })
        {
            buffer.AppendLine("现有释义（供你理解词义，不要输出）：");
            foreach (var pair in glossesByReading)
            {
                var label = string.IsNullOrEmpty(pair.Key) ? "（无读音）" : pair.Key;
                buffer.AppendLine($"- {label}：{string.Join("；", pair.Value)}");
            }

            buffer.AppendLine();
        }

        buffer.AppendLine("需要补充的组合：");
        foreach (var gap in gaps)
        {
            var reading = string.IsNullOrEmpty(gap.Reading) ? "（无读音）" : gap.Reading;
            var pos = string.IsNullOrEmpty(gap.PartOfSpeech) ? "未指定词性" : gap.PartOfSpeech;
            buffer.AppendLine($"- 读音「{reading}」词性「{pos}」：已有 {gap.Have} 条，请再给 {gap.Need} 条");
        }

        buffer.AppendLine();
        buffer.AppendLine("请只输出补的这些例句的 JSON。");
        return buffer.ToString();
    }

    /// <summary>网页版粘贴用：把两段合并成一次性可复制的文本。</summary>
    public static string ClipboardPrompt(string term, string language, IReadOnlyList<ExampleGap> gaps)
        => $"""
        请严格按下面的要求处理，只输出 JSON。

        {SystemPrompt()}

        ---
        {UserPrompt(term, language, gaps)}
        """;
}

public static class ExampleTopUpParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static TopUpParseResult Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new TopUpParseResult(Array.Empty<ExampleAddition>(), "模型没有返回内容。");
        }

        var json = EntryJsonParser.ExtractJsonObject(raw);
        if (json is null)
        {
            return new TopUpParseResult(Array.Empty<ExampleAddition>(), "返回内容里找不到 JSON 对象。");
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(json, Options);
        }
        catch (JsonException ex)
        {
            return new TopUpParseResult(Array.Empty<ExampleAddition>(), $"JSON 解析失败：{ex.Message}");
        }

        if (payload is null)
        {
            return new TopUpParseResult(Array.Empty<ExampleAddition>(), "JSON 解析结果为空。");
        }

        if (!string.IsNullOrWhiteSpace(payload.error))
        {
            return new TopUpParseResult(Array.Empty<ExampleAddition>(), $"模型拒绝补充：{payload.error}");
        }

        var additions = new List<ExampleAddition>();
        foreach (var group in payload.additions ?? new List<GroupPayload>())
        {
            var reading = (group.reading ?? string.Empty).Trim();
            var pos = (group.pos ?? string.Empty).Trim();
            foreach (var example in group.examples ?? new List<ExamplePayload>())
            {
                if (string.IsNullOrWhiteSpace(example.text))
                {
                    continue;
                }

                additions.Add(new ExampleAddition(
                    reading,
                    pos,
                    example.text!.Trim(),
                    string.IsNullOrWhiteSpace(example.translation) ? null : example.translation!.Trim(),
                    string.IsNullOrWhiteSpace(example.note) ? null : example.note!.Trim()));
            }
        }

        // 兼容模型直接给一个顶层 examples 数组的情况
        foreach (var example in payload.examples ?? new List<ExamplePayload>())
        {
            if (string.IsNullOrWhiteSpace(example.text))
            {
                continue;
            }

            additions.Add(new ExampleAddition(
                (example.reading ?? string.Empty).Trim(),
                (example.pos ?? string.Empty).Trim(),
                example.text!.Trim(),
                string.IsNullOrWhiteSpace(example.translation) ? null : example.translation!.Trim(),
                string.IsNullOrWhiteSpace(example.note) ? null : example.note!.Trim()));
        }

        return additions.Count == 0
            ? new TopUpParseResult(additions, "模型没有给出可用例句。")
            : new TopUpParseResult(additions, null);
    }

    private sealed class Payload
    {
        public string? error { get; set; }
        public List<GroupPayload>? additions { get; set; }
        public List<ExamplePayload>? examples { get; set; }
    }

    private sealed class GroupPayload
    {
        public string? reading { get; set; }
        public string? pos { get; set; }
        public List<ExamplePayload>? examples { get; set; }
    }

    private sealed class ExamplePayload
    {
        public string? reading { get; set; }
        public string? pos { get; set; }
        public string? text { get; set; }
        public string? translation { get; set; }
        public string? note { get; set; }
    }
}
