using System.Text.Json;
using System.Text.Json.Serialization;
using WordBase.Core.Models;
using WordBase.Core.Text;

namespace WordBase.Llm;

public sealed record LlmParseResult(Entry? Entry, string? Error)
{
    public bool Ok => Entry is not null;
}

/// <summary>
/// 容错解析模型输出：可能带 ```json 代码块、前后废话、大小写不同的键、多余的字段。
/// 解析失败时不抛异常，而是返回错误说明，界面据此提示用户重试或改用手填。
/// </summary>
public static class EntryJsonParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static LlmParseResult Parse(string raw, string fallbackLanguage, EntryKind fallbackKind,
        EntrySource source)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new LlmParseResult(null, "模型没有返回内容。");
        }

        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            return new LlmParseResult(null, "返回内容里找不到 JSON 对象。");
        }

        EntryPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<EntryPayload>(json, Options);
        }
        catch (JsonException ex)
        {
            return new LlmParseResult(null, $"JSON 解析失败：{ex.Message}");
        }

        if (payload is null)
        {
            return new LlmParseResult(null, "JSON 解析结果为空。");
        }

        if (!string.IsNullOrWhiteSpace(payload.error))
        {
            return new LlmParseResult(null, $"模型认为这不是有效词条：{payload.error}");
        }

        var entry = Map(payload, fallbackLanguage, fallbackKind, source);
        if (string.IsNullOrWhiteSpace(entry.Term))
        {
            return new LlmParseResult(null, "模型没有给出词条原文。");
        }

        return new LlmParseResult(entry, null);
    }

    /// <summary>从任意文本里截出第一个完整的 JSON 对象（大括号配对，且跳过字符串内的括号）。</summary>
    public static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return raw[start..(i + 1)];
                    }

                    break;
            }
        }

        return null;
    }

    private static Entry Map(EntryPayload payload, string fallbackLanguage, EntryKind fallbackKind,
        EntrySource source)
    {
        var entry = new Entry
        {
            Term = TermNormalizer.Tidy(payload.term),
            Language = string.IsNullOrWhiteSpace(payload.language)
                ? fallbackLanguage
                : payload.language!.Trim().ToLowerInvariant(),
            Kind = ParseKind(payload.kind, fallbackKind),
            Note = string.IsNullOrWhiteSpace(payload.note) ? null : payload.note!.Trim(),
            Source = source,
        };
        entry.TermNormalized = TermNormalizer.Normalize(entry.Term);

        var readingTempId = -1L;
        var senseTempId = -1L;
        var exampleOrdinal = 0;

        if (payload.readings is { Count: > 0 })
        {
            foreach (var readingPayload in payload.readings)
            {
                var reading = new Reading
                {
                    Id = readingTempId--,
                    Text = TermNormalizer.Tidy(readingPayload.text),
                    IsCommon = readingPayload.isCommon ?? true,
                    Ordinal = entry.Readings.Count,
                    Note = string.IsNullOrWhiteSpace(readingPayload.note) ? null : readingPayload.note!.Trim(),
                };
                entry.Readings.Add(reading);

                foreach (var gloss in readingPayload.senses ?? new List<string>())
                {
                    AddSense(entry, gloss, reading.Id, ref senseTempId);
                }

                foreach (var examplePayload in readingPayload.examples ?? new List<ExamplePayload>())
                {
                    AddExample(entry, examplePayload, reading.Id, source, ref exampleOrdinal);
                }
            }
        }

        // 兼容模型不按读音分组、直接给顶层 senses/examples 的情况。
        foreach (var gloss in payload.senses ?? new List<string>())
        {
            AddSense(entry, gloss, entry.Readings.FirstOrDefault()?.Id, ref senseTempId);
        }

        foreach (var examplePayload in payload.examples ?? new List<ExamplePayload>())
        {
            AddExample(entry, examplePayload, entry.Readings.FirstOrDefault()?.Id, source, ref exampleOrdinal);
        }

        if (entry.Senses.Count == 0 && !string.IsNullOrWhiteSpace(payload.meaning))
        {
            AddSense(entry, payload.meaning!, entry.Readings.FirstOrDefault()?.Id, ref senseTempId);
        }

        return entry;
    }

    private static void AddSense(Entry entry, string? gloss, long? readingId, ref long senseTempId)
    {
        if (string.IsNullOrWhiteSpace(gloss))
        {
            return;
        }

        entry.Senses.Add(new Sense
        {
            Id = senseTempId--,
            ReadingId = readingId,
            Gloss = gloss.Trim(),
            Ordinal = entry.Senses.Count,
            IsVerified = false,
        });
    }

    private static void AddExample(Entry entry, ExamplePayload payload, long? readingId, EntrySource source,
        ref int ordinal)
    {
        if (string.IsNullOrWhiteSpace(payload.text))
        {
            return;
        }

        entry.Examples.Add(new Example
        {
            ReadingId = readingId,
            PartOfSpeech = string.IsNullOrWhiteSpace(payload.pos) ? null : payload.pos!.Trim(),
            Text = payload.text!.Trim(),
            Translation = string.IsNullOrWhiteSpace(payload.translation) ? null : payload.translation!.Trim(),
            Note = string.IsNullOrWhiteSpace(payload.note) ? null : payload.note!.Trim(),
            Source = source,
            IsVerified = false,
            Ordinal = ordinal++,
        });
    }

    private static EntryKind ParseKind(string? raw, EntryKind fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var text = raw.Trim().ToLowerInvariant();
        if (text.Contains("grammar") || text.Contains("语法"))
        {
            return EntryKind.Grammar;
        }

        if (text.Contains("word") || text.Contains("单词"))
        {
            return EntryKind.Word;
        }

        return fallback;
    }

    private sealed class EntryPayload
    {
        public string? term { get; set; }
        public string? language { get; set; }
        public string? kind { get; set; }
        public string? note { get; set; }
        public string? meaning { get; set; }
        public string? error { get; set; }
        public List<ReadingPayload>? readings { get; set; }
        public List<string>? senses { get; set; }
        public List<ExamplePayload>? examples { get; set; }
    }

    private sealed class ReadingPayload
    {
        public string? text { get; set; }
        public bool? isCommon { get; set; }
        public string? note { get; set; }
        public List<string>? senses { get; set; }
        public List<ExamplePayload>? examples { get; set; }
    }

    private sealed class ExamplePayload
    {
        public string? pos { get; set; }
        public string? text { get; set; }
        public string? translation { get; set; }
        public string? note { get; set; }
    }
}
