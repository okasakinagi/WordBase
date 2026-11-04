using WordBase.Core.Models;
using WordBase.Core.Text;

namespace WordBase.Core.Validation;

public enum IssueSeverity
{
    Warning = 0,
    Error = 1,
}

public sealed record ValidationIssue(IssueSeverity Severity, string Code, string Message);

public sealed class ValidationResult
{
    public List<ValidationIssue> Issues { get; } = new();

    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == IssueSeverity.Warning);
    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);
    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => i.Severity == IssueSeverity.Warning);

    public void Error(string code, string message) => Issues.Add(new(IssueSeverity.Error, code, message));
    public void Warn(string code, string message) => Issues.Add(new(IssueSeverity.Warning, code, message));
}

/// <summary>
/// 例句目标：每个「读音 × 词性」至少 <see cref="ExamplesPerReadingPos"/> 句。
/// 这只是**目标**：大模型路径在提示词里强制要求，人工路径只警告不拦截。
/// </summary>
public sealed class EntryValidator
{
    public int ExamplesPerReadingPos { get; init; } = 2;
    public int ExamplesPerEntry { get; init; } = 2;

    public ValidationResult Validate(Entry entry)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(entry.Term))
        {
            result.Error("term.required", "词条不能为空。");
        }

        var readings = entry.Readings.Where(r => !string.IsNullOrWhiteSpace(r.Text)).ToList();
        var duplicateReading = readings
            .GroupBy(r => TermNormalizer.Normalize(r.Text))
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateReading is not null)
        {
            result.Warn("reading.duplicate", $"读音「{duplicateReading.First().Text}」重复了。");
        }

        var senses = entry.Senses.Where(s => !string.IsNullOrWhiteSpace(s.Gloss)).ToList();
        if (senses.Count == 0)
        {
            result.Error("sense.required", "至少需要一个释义。");
        }

        var examples = entry.Examples.Where(e => !string.IsNullOrWhiteSpace(e.Text)).ToList();
        if (examples.Count == 0)
        {
            result.Error("example.required", "至少需要一个例句。");
        }

        if (entry.Examples.Any(e => string.IsNullOrWhiteSpace(e.Text) &&
                                    (!string.IsNullOrWhiteSpace(e.Translation) ||
                                     !string.IsNullOrWhiteSpace(e.PartOfSpeech))))
        {
            result.Error("example.text", "有条目只填了译文或词性，缺少例句原文。");
        }

        if (entry.Readings.Any(r => string.IsNullOrWhiteSpace(r.Text)))
        {
            result.Warn("reading.blank", "存在空读音，保存时会忽略。");
        }

        if (examples.Count > 0 && examples.Count < ExamplesPerEntry)
        {
            result.Warn("example.few", $"整条词条只有 {examples.Count} 个例句，目标至少 {ExamplesPerEntry} 个。");
        }

        foreach (var cell in Coverage(entry).Where(c => !c.Ok))
        {
            result.Warn("example.coverage", $"「{cell.Display}」例句不足。");
        }

        if (entry.Senses.Any(s => !s.IsVerified) || entry.Examples.Any(e => !e.IsVerified))
        {
            result.Warn("content.unverified", "含未校对的大模型生成内容，请确认后再作为记忆依据。");
        }

        return result;
    }

    /// <summary>「读音 × 词性」覆盖矩阵，用于界面显示缺口。</summary>
    public IReadOnlyList<CoverageCell> Coverage(Entry entry)
    {
        var readings = entry.Readings.Where(r => !string.IsNullOrWhiteSpace(r.Text)).ToList();
        var examples = entry.Examples.Where(e => !string.IsNullOrWhiteSpace(e.Text)).ToList();
        var cells = new List<CoverageCell>();

        // 有读音时逐读音统计；没有读音时按「整条词 × 词性」统计。
        if (readings.Count == 0)
        {
            foreach (var group in examples.GroupBy(e => NormalizePos(e.PartOfSpeech)))
            {
                cells.Add(new CoverageCell(string.Empty, group.Key, group.Count(), ExamplesPerReadingPos));
            }
            return cells;
        }

        foreach (var reading in readings)
        {
            var grouped = examples
                .Where(e => e.ReadingId == reading.Id || e.ReadingId is null)
                .GroupBy(e => NormalizePos(e.PartOfSpeech));

            var any = false;
            foreach (var group in grouped)
            {
                any = true;
                cells.Add(new CoverageCell(reading.Text, group.Key, group.Count(), ExamplesPerReadingPos));
            }

            if (!any)
            {
                cells.Add(new CoverageCell(reading.Text, string.Empty, 0, ExamplesPerReadingPos));
            }
        }

        return cells;
    }

    private static string NormalizePos(string? pos) => (pos ?? string.Empty).Trim().ToLowerInvariant();
}

public sealed record CoverageCell(string Reading, string PartOfSpeech, int Count, int Target)
{
    public bool Ok => Count >= Target;

    public string Display =>
        $"{(Reading.Length == 0 ? "（无读音）" : Reading)} · " +
        $"{(PartOfSpeech.Length == 0 ? "未标词性" : PartOfSpeech)}：{Count}/{Target}";
}
