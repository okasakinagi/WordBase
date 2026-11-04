namespace WordBase.Core.Models;

public sealed class Reading
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public string Text { get; set; } = string.Empty;

    /// <summary>是否常用读音。由模型建议、用户确认。</summary>
    public bool IsCommon { get; set; } = true;

    public int Ordinal { get; set; }
    public string? Note { get; set; }
}

public sealed class Sense
{
    public long Id { get; set; }
    public long EntryId { get; set; }

    /// <summary>归属读音；为空表示整个词条共用。</summary>
    public long? ReadingId { get; set; }

    public int Ordinal { get; set; }
    public string Gloss { get; set; } = string.Empty;
    public string? Note { get; set; }

    /// <summary>由大模型生成且用户尚未校对。</summary>
    public bool IsVerified { get; set; } = true;
}

public sealed class Example
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public long? ReadingId { get; set; }
    public long? SenseId { get; set; }

    /// <summary>词性体现在例句上（名词/动词/一类形容词…）。</summary>
    public string? PartOfSpeech { get; set; }

    public string Text { get; set; } = string.Empty;
    public string? Translation { get; set; }
    public string? Note { get; set; }
    public EntrySource Source { get; set; } = EntrySource.Manual;
    public bool IsVerified { get; set; } = true;
    public int Ordinal { get; set; }
}

public sealed class ReviewState
{
    public long EntryId { get; set; }
    public CardState State { get; set; } = CardState.New;
    public DateTimeOffset? DueAt { get; set; }
    public double Stability { get; set; }
    public double Difficulty { get; set; }
    public int Reps { get; set; }
    public int Lapses { get; set; }
    public DateTimeOffset? LastReviewAt { get; set; }
    public int ElapsedDays { get; set; }
    public int ScheduledDays { get; set; }

    public ReviewState Clone() => (ReviewState)MemberwiseClone();
}

public sealed class ReviewLog
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public DateTimeOffset ReviewedAt { get; set; }

    /// <summary>交互来源，用于区分 认识/不认识/选择题。</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>原始置信度（0..1），保留它以后可无损升级为软标签算法。</summary>
    public double? Confidence { get; set; }

    public Rating Rating { get; set; }
    public CardState PrevState { get; set; }
    public double PrevStability { get; set; }
    public double PrevDifficulty { get; set; }
    public double NextStability { get; set; }
    public double NextDifficulty { get; set; }
    public DateTimeOffset NextDueAt { get; set; }
    public int ResponseMs { get; set; }
}

public sealed class DuplicateEvent
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public DateTimeOffset SeenAt { get; set; }
    public DuplicateChannel Channel { get; set; }
    public string? Query { get; set; }
}
