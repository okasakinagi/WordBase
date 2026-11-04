namespace WordBase.Core.Models;

public enum EntryKind
{
    Word = 0,
    Grammar = 1,
}

public enum EntrySource
{
    Manual = 0,
    LlmAuto = 1,
    LlmPaste = 2,
    Import = 3,
}

public enum CardState
{
    New = 0,
    Learning = 1,
    Review = 2,
    Relearning = 3,
}

/// <summary>FSRS 四档评分，数值必须与 1..4 对齐。</summary>
public enum Rating
{
    Again = 1,
    Hard = 2,
    Good = 3,
    Easy = 4,
}

public enum DuplicateChannel
{
    Manual = 0,
    LlmAuto = 1,
    LlmPaste = 2,
    Import = 3,
    Lookup = 4,
}
