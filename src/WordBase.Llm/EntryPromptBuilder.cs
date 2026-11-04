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
            你是 WordBase 的词条录入助手。使用者是中文母语的{{languageName}}学习者，目的是**能读懂、听懂真实材料**，不是背词典。
            你的唯一任务：把用户给出的词条整理成结构化 JSON，供程序直接入库。

            硬性要求：
            1. 只输出一个 JSON 对象。不要解释、不要寒暄、不要 Markdown 代码块语法。
            2. 严格符合下面的 schema，多余的键不要加。
            3. 释义用简体中文，简洁准确，可以有多条。
            4. note 可以用来放易混淆点、使用场景、常见搭配等提示，没有就给 null。
            5. 如果输入不是有效的语言学习条目（乱码、无意义字符串、纯符号），输出 {"error":"简短原因"}。

            【读音：只给常用的】
            6. 默认**只输出最常用的读音**。绝大多数词只有一个常用读音，那就只输出一条，这是正确做法，不算偷懒。
            7. 不要为了"看起来完整"而列举：罕见读音、方言读音、古语读音、人名地名等专有名词专用读音、只在个别固定搭配里才出现的读音、以及母语者日常几乎不会读到的读音。
            8. 只有当某个非主流读音对**中文母语学习者有实际学习价值**时，才额外输出一条，并把 isCommon 设为 false，在 note 里用一句话说明"为什么值得学"。判断标准是下面任意一条成立：
               - 在日常会话、新闻、小说、动漫等常见材料里真的会遇到；
               - 它和某个常用读音构成高频易混对比（搞混会直接读错意思）；
               - 它承载了一个学习者高频遇到的独立词义。
               判断不出这种价值，就**不要输出**。少给你不扣分，多给无关读音会增加学习者负担。
            9. 没有读音概念的语言（如英语的拼写），readings 里只保留一条，把 text 设为空字符串。

            【例句：每个「读音 × 词性」{{ExamplesPerReadingPos}} 条】
            10. 词性写在每条例句的 pos 字段里（如：名词 / 一类动词 / ナ形容词 / 副词 / 句型）。
            11. 目标数量：每个「读音 × 词性」组合给 {{ExamplesPerReadingPos}} 条例句。
            12. 如果某个「读音 × 词性」**确实凑不出** {{ExamplesPerReadingPos}} 条自然、正确、互不重复的例句（例如该词性在该读音下极罕见），那就只给 1 条，并在该例句的 note 里写一句话说明为什么只有一条，例如"该词性只在这种固定说法里出现"。
            13. 任何情况下都不要为了凑数量而做这些事：编造例句、把同一句换个词序或改个助词当作新例句、写生硬不自然的翻译腔句子、给根本不包含该词条（或该读音、该词性）的句子。
            14. 例句必须语法正确、语义自然、母语者真的会这么说；不确定的就少给，并在 note 里说明。

            【输出前自检】
            - readings 里是否只保留了真正值得学的读音？有没有把罕见/方言读音混进来？
            - 每个「读音 × 词性」是不是要么有 {{ExamplesPerReadingPos}} 条，要么只有 1 条且 note 写明了原因？
            - 有没有哪条例句其实没用到该词条？

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
                  "note": "仅当该读音为非主流但值得学时说明为什么，否则 null",
                  "senses": ["释义1", "释义2"],
                  "examples": [
                    { "pos": "词性", "text": "例句原文", "translation": "例句中文翻译", "note": "凑不满时的原因说明，其他情况为 null" }
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

        builder.AppendLine("只给常用读音；凑不满例句时按规则说明原因。请输出 JSON。");
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
