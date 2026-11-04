using WordBase.Core.Models;
using WordBase.Llm;
using Xunit;

namespace WordBase.Core.Tests;

public class EntryJsonParserTests
{
    private const string SampleJson = """
        {
          "term": "生",
          "language": "ja",
          "kind": "word",
          "note": null,
          "readings": [
            {
              "text": "せい",
              "isCommon": true,
              "senses": ["生命；生存", "学生的「生」"],
              "examples": [
                { "pos": "名词", "text": "人生は一度きりだ。", "translation": "人生只有一次。" },
                { "pos": "名词", "text": "生徒が集まる。", "translation": "学生们聚集起来。" }
              ]
            },
            {
              "text": "なま",
              "isCommon": true,
              "senses": ["生的；未加工的"],
              "examples": [
                { "pos": "ナ形容词", "text": "生の魚は苦手だ。", "translation": "我不太能接受生鱼。" },
                { "pos": "ナ形容词", "text": "生のデータを見る。", "translation": "查看原始数据。" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsNestedReadings()
    {
        var result = EntryJsonParser.Parse(SampleJson, "ja", EntryKind.Word, EntrySource.LlmAuto);

        Assert.True(result.Ok);
        var entry = result.Entry!;
        Assert.Equal("生", entry.Term);
        Assert.Equal("ja", entry.Language);
        Assert.Equal(2, entry.Readings.Count);
        Assert.Equal(3, entry.Senses.Count);
        Assert.Equal(4, entry.Examples.Count);
        Assert.All(entry.Senses, s => Assert.False(s.IsVerified));
        Assert.All(entry.Examples, e => Assert.False(e.IsVerified));
        Assert.Equal(EntrySource.LlmAuto, entry.Source);
    }

    [Fact]
    public void Parse_LinksSensesAndExamplesToReadings()
    {
        var entry = EntryJsonParser.Parse(SampleJson, "ja", EntryKind.Word, EntrySource.LlmAuto).Entry!;
        var second = entry.Readings[1];

        Assert.Equal(1, entry.Senses.Count(s => s.ReadingId == second.Id));
        Assert.All(entry.Examples.Where(e => e.ReadingId == second.Id), e => Assert.Equal("ナ形容词", e.PartOfSpeech));
    }

    [Fact]
    public void Parse_HandlesFencedAndChattyOutput()
    {
        var raw = "好的，下面是结果：\n```json\n" + SampleJson + "\n```\n希望有帮助。";
        var result = EntryJsonParser.Parse(raw, "ja", EntryKind.Word, EntrySource.LlmPaste);
        Assert.True(result.Ok);
        Assert.Equal(2, result.Entry!.Readings.Count);
    }

    [Fact]
    public void Parse_ReturnsErrorPayloadAsFailure()
    {
        var result = EntryJsonParser.Parse("""{"error":"这不是一个词"}""", "ja", EntryKind.Word, EntrySource.LlmAuto);
        Assert.False(result.Ok);
        Assert.Contains("不是有效词条", result.Error);
    }

    [Fact]
    public void Parse_FallsBackToFlatSensesAndExamples()
    {
        var raw = """
            {
              "term": "run",
              "senses": ["跑"],
              "examples": [{ "pos": "verb", "text": "I run every day.", "translation": "我每天跑步。" }]
            }
            """;

        var result = EntryJsonParser.Parse(raw, "en", EntryKind.Word, EntrySource.LlmAuto);
        Assert.True(result.Ok);
        Assert.Single(result.Entry!.Senses);
        Assert.Single(result.Entry!.Examples);
        Assert.Null(result.Entry!.Examples[0].ReadingId);
    }

    [Fact]
    public void Parse_NoJson_ReturnsFailure()
    {
        var result = EntryJsonParser.Parse("抱歉，我无法回答。", "en", EntryKind.Word, EntrySource.LlmAuto);
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ExtractJsonObject_IgnoresBracesInsideStrings()
    {
        var json = EntryJsonParser.ExtractJsonObject("""{"a":"}{","b":1}""");
        Assert.Equal("""{"a":"}{","b":1}""", json);
    }

    [Fact]
    public void ClipboardPrompt_ContainsSchemaAndTerm()
    {
        var prompt = EntryPromptBuilder.ClipboardPrompt("生", "ja", EntryKind.Word);
        Assert.Contains("生", prompt);
        Assert.Contains("\"readings\"", prompt);
        Assert.Contains("日语", prompt);
    }
}
