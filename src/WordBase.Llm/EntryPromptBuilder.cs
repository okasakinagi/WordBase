using System.Text;
using WordBase.Core.Models;

namespace WordBase.Llm;

/// <summary>提示词模板。结构完全由 schema 约束，便于无论走接口还是网页粘贴都能被同一套解析器吃下。</summary>
public static class EntryPromptBuilder
{
    public const int ExamplesPerReadingPos = 2;

    public static string SystemPrompt(string languageCode, EntryKind kind)
    {
        var languageName = LanguageCatalog.PromptName(languageCode);
        var kindText = kind == EntryKind.Grammar ? "语法点（句型 / 接续 / 语法条目）" : "单词（可以是固定搭配）";

        return $$"""
            你是 WordBase 的词条录入助手。使用者是中文母语的{{languageName}}学习者。
            你的唯一任务：把用户给出的词条整理成结构化 JSON，供程序直接入库。

            硬性要求：
            1. 只输出一个 JSON 对象。不要解释、不要寒暄、不要 Markdown 代码块语法。
            2. 严格符合下面的 schema，多余的键不要加。
            3. 例句必须语法正确、语义自然、母语者真的会这么说。宁可少给，也绝不编造错误或生硬的句子；如果你对某个例句没有把握，就少给一条。
            4. 该词条可能有多个读音（日语同形异读很常见），逐个放进 readings；isCommon 表示是否为母语者最常用的读音（最多两个标 true）。没有读音概念的语言（如英语拼写）把 text 设为空字符串。
            5. 每个「读音 × 词性」尽量给 {{ExamplesPerReadingPos}} 个例句；词性写在每条例句的 pos 字段里（如：名词 / 一类动词 / ナ形容词 / 副词 / 句型）。如果确实凑不满，就按实际给，不要凑数。
            6. 释义用简体中文，简洁准确，可以有多条。
            7. note 可以用来放易混淆点、使用场景、常见搭配等提示，没有就给 null。
            8. 如果输入不是有效的语言学习条目（乱码、无意义字符串、纯符号），输出 {"error":"简短原因"}。

            输出 schema：
            {
              "term": "词条原文",
              "language": "语言代码，例如 ja",
              "kind": "word 或 grammar",
              "note": "补充说明或 null",
              "readings": [
                {
                  "text": "读音 / 假名 / 音标，没有就空字符串",
                  "isCommon": true,
                  "senses": ["释义1", "释义2"],
                  "examples": [
                    { "pos": "词性", "text": "例句原文", "translation": "例句中文翻译" }
                  ]
                }
              ]
            }

            本次词条的语言是 {{languageName}}，类型是 {{kindText}}。
            """;
    }

    public static string UserPrompt(string term, string languageCode, EntryKind kind, string? extraHint = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"要录入的词条：{term}");
        builder.AppendLine($"语言：{LanguageCatalog.PromptName(languageCode)}");
        builder.AppendLine($"类型：{(kind == EntryKind.Grammar ? "语法" : "单词")}");
        if (!string.IsNullOrWhiteSpace(extraHint))
        {
            builder.AppendLine($"补充信息（用户提供的上下文，可参考但不要盲信）：{extraHint}");
        }

        builder.AppendLine("请输出 JSON。");
        return builder.ToString();
    }

    /// <summary>给「网页粘贴」路径用：把 system + user 合成一段可以直接丢进任意聊天窗口的文本。</summary>
    public static string ClipboardPrompt(string term, string languageCode, EntryKind kind, string? extraHint = null) =>
        $"""
        请严格按下面的要求处理，只输出 JSON。

        {SystemPrompt(languageCode, kind)}

        ---
        {UserPrompt(term, languageCode, kind, extraHint)}
        """;
}
