namespace WordBase.Core.Models;

public sealed class Entry
{
    public long Id { get; set; }
    public string Language { get; set; } = "en";
    public EntryKind Kind { get; set; } = EntryKind.Word;
    public string Term { get; set; } = string.Empty;
    public string TermNormalized { get; set; } = string.Empty;
    public string? Note { get; set; }
    public EntrySource Source { get; set; } = EntrySource.Manual;
    public int HitCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Reading> Readings { get; } = new();
    public List<Sense> Senses { get; } = new();
    public List<Example> Examples { get; } = new();
    public ReviewState Review { get; set; } = new();

    public string PrimaryGloss =>
        Senses.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Gloss))?.Gloss ?? string.Empty;

    public string ReadingsDisplay =>
        string.Join(" / ", Readings.Where(r => !string.IsNullOrWhiteSpace(r.Text)).Select(r => r.Text));

    public IReadOnlyList<string> DistinctPartsOfSpeech =>
        Examples.Select(e => (e.PartOfSpeech ?? string.Empty).Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>克隆一份但清空所有数据库标识，用于草稿流转。</summary>
    public Entry ToDraftCopy()
    {
        var copy = new Entry
        {
            Language = Language,
            Kind = Kind,
            Term = Term,
            TermNormalized = TermNormalized,
            Note = Note,
            Source = Source,
        };
        foreach (var r in Readings)
        {
            copy.Readings.Add(new Reading { Text = r.Text, IsCommon = r.IsCommon, Ordinal = r.Ordinal, Note = r.Note });
        }
        foreach (var s in Senses)
        {
            copy.Senses.Add(new Sense { Gloss = s.Gloss, Note = s.Note, Ordinal = s.Ordinal, IsVerified = s.IsVerified });
        }
        foreach (var e in Examples)
        {
            copy.Examples.Add(new Example
            {
                PartOfSpeech = e.PartOfSpeech,
                Text = e.Text,
                Translation = e.Translation,
                Note = e.Note,
                Source = e.Source,
                IsVerified = e.IsVerified,
                Ordinal = e.Ordinal,
            });
        }
        return copy;
    }
}

/// <summary>列表展示用的轻量投影，避免加载整棵子实体。</summary>
public sealed record EntrySummary(
    long Id,
    string Language,
    EntryKind Kind,
    string Term,
    string Readings,
    string PrimaryGloss,
    int ExampleCount,
    DateTimeOffset? DueAt,
    CardState State);
