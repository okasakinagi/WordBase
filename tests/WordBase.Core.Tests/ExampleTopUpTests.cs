using WordBase.Core.Models;
using WordBase.Llm;
using Xunit;

namespace WordBase.Core.Tests;

public class ExampleTopUpTests
{
    private const string AdditionsJson = """
        {
          "additions": [
            {
              "reading": "せい",
              "pos": "名词",
              "examples": [
                { "text": "先生に相談した。", "translation": "和老师商量了。", "note": null },
                { "text": "人生の意味を考える。", "translation": "思考人生的意义。", "note": null }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsGroupedAdditions()
    {
        var result = ExampleTopUpParser.Parse(AdditionsJson);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Additions.Count);
        Assert.All(result.Additions, a => Assert.Equal("せい", a.Reading));
        Assert.All(result.Additions, a => Assert.Equal("名词", a.PartOfSpeech));
        Assert.Equal("先生に相談した。", result.Additions[0].Text);
        Assert.Equal("和老师商量了。", result.Additions[0].Translation);
    }

    [Fact]
    public void Parse_HandlesFencedOutput()
    {
        var raw = "补好了：\n```json\n" + AdditionsJson + "\n```";
        Assert.True(ExampleTopUpParser.Parse(raw).Ok);
    }

    [Fact]
    public void Parse_AcceptsFlatExampleArray()
    {
        var raw = """
            { "examples": [ { "reading": "なま", "pos": "ナ形容词", "text": "生のまま食べる。", "translation": "生着吃。" } ] }
            """;

        var result = ExampleTopUpParser.Parse(raw);
        Assert.True(result.Ok);
        Assert.Equal("なま", result.Additions[0].Reading);
        Assert.Equal("ナ形容词", result.Additions[0].PartOfSpeech);
    }

    [Fact]
    public void Parse_SkipsBlankText()
    {
        var raw = """{ "additions": [ { "reading": "せい", "pos": "名词", "examples": [ { "text": "  " } ] } ] }""";
        Assert.False(ExampleTopUpParser.Parse(raw).Ok);
    }

    [Fact]
    public void Parse_NoJson_IsFailure()
    {
        var result = ExampleTopUpParser.Parse("我无法补充。");
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Parse_ErrorPayloadIsFailure()
    {
        var result = ExampleTopUpParser.Parse("""{"error":"该词没有能补充的词性"}""");
        Assert.False(result.Ok);
        Assert.Contains("拒绝补充", result.Error);
    }

    [Fact]
    public void UserPrompt_ListsEachGapWithCounts()
    {
        var gaps = new[]
        {
            new ExampleGap("せい", "名词", 1, 1),
            new ExampleGap(string.Empty, "verb", 0, 2),
        };

        var prompt = ExampleTopUpPromptBuilder.UserPrompt("生", "ja", gaps);

        Assert.Contains("せい", prompt);
        Assert.Contains("已有 1 条", prompt);
        Assert.Contains("请再给 1 条", prompt);
        Assert.Contains("（无读音）", prompt);
        Assert.Contains("请再给 2 条", prompt);
    }

    [Fact]
    public void ClipboardPrompt_MentionsAdditionsSchema()
    {
        var prompt = ExampleTopUpPromptBuilder.ClipboardPrompt("生", "ja", new[] { new ExampleGap("せい", "名词", 0, 2) });
        Assert.Contains("additions", prompt);
        Assert.Contains("不要重写释义", prompt);
    }
}

public class EntryPromptPolicyTests
{
    [Fact]
    public void SystemPrompt_AsksForCommonReadingsOnly()
    {
        var prompt = EntryPromptBuilder.SystemPrompt("ja", EntryKind.Word);

        Assert.Contains("只输出最常用的读音", prompt);
        Assert.Contains("方言读音", prompt);
        Assert.Contains("isCommon 设为 false", prompt);
        Assert.Contains("不要输出", prompt);
    }

    [Fact]
    public void SystemPrompt_TellsModelWhatToDoWhenExamplesAreShort()
    {
        var prompt = EntryPromptBuilder.SystemPrompt("ja", EntryKind.Word);

        Assert.Contains("确实凑不出", prompt);
        Assert.Contains("note 里写一句话说明为什么只有一条", prompt);
        Assert.Contains("不要为了凑数量", prompt);
    }

    [Fact]
    public void SystemPrompt_KeepsExampleSchemaWithNote()
    {
        var prompt = EntryPromptBuilder.SystemPrompt("en", EntryKind.Word);
        Assert.Contains("\"note\": \"凑不满时的原因说明", prompt);
    }
}
