using WordBase.Core.Abstractions;
using WordBase.Core.Models;
using WordBase.Core.Text;
using WordBase.Core.Validation;

namespace WordBase.Core.Services;

public enum SaveStatus
{
    Saved = 0,
    Duplicate = 1,
    Invalid = 2,
}

public sealed class SaveResult
{
    public SaveStatus Status { get; init; }
    public long EntryId { get; init; }
    public Entry? Duplicate { get; init; }
    public ValidationResult Validation { get; init; } = new();
    public IReadOnlyList<CoverageCell> Coverage { get; init; } = Array.Empty<CoverageCell>();

    public bool Ok => Status == SaveStatus.Saved;
}

public sealed class VocabularyService
{
    private readonly IEntryStore _store;
    private readonly EntryValidator _validator;
    private readonly Func<DateTimeOffset> _clock;

    public VocabularyService(IEntryStore store, EntryValidator? validator = null, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _validator = validator ?? new EntryValidator();
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public EntryValidator Validator => _validator;

    public Entry? Lookup(string language, string term)
    {
        var normalized = TermNormalizer.Normalize(term);
        return normalized.Length == 0 ? null : _store.FindByTerm(language, normalized);
    }

    public SaveResult Save(Entry entry, DuplicateChannel channel = DuplicateChannel.Manual)
    {
        entry.Term = TermNormalizer.Tidy(entry.Term);
        entry.TermNormalized = TermNormalizer.Normalize(entry.Term);
        entry.Language = string.IsNullOrWhiteSpace(entry.Language) ? "en" : entry.Language.Trim().ToLowerInvariant();

        var validation = _validator.Validate(entry);
        var coverage = _validator.Coverage(entry);
        if (validation.HasErrors)
        {
            return new SaveResult { Status = SaveStatus.Invalid, Validation = validation, Coverage = coverage };
        }

        var existing = _store.FindByTerm(entry.Language, entry.TermNormalized);
        if (existing is not null && existing.Id != entry.Id)
        {
            _store.RecordEncounter(existing.Id, channel, entry.Term, _clock());
            return new SaveResult
            {
                Status = SaveStatus.Duplicate,
                Duplicate = _store.Get(existing.Id),
                Validation = validation,
                Coverage = coverage,
            };
        }

        var now = _clock();
        if (entry.Id == 0)
        {
            entry.CreatedAt = now;
            entry.UpdatedAt = now;
            var id = _store.Create(entry);
            return new SaveResult { Status = SaveStatus.Saved, EntryId = id, Validation = validation, Coverage = coverage };
        }

        entry.UpdatedAt = now;
        _store.Update(entry);
        return new SaveResult { Status = SaveStatus.Saved, EntryId = entry.Id, Validation = validation, Coverage = coverage };
    }

    /// <summary>用户在主界面「查词」时确认看到同一条词，记一次遇见以便加强。</summary>
    public void RecordEncounter(long entryId, DuplicateChannel channel, string? query) =>
        _store.RecordEncounter(entryId, channel, query, _clock());

    public IReadOnlyList<EntrySummary> Search(string? query, string? language, int limit = 200, int offset = 0) =>
        _store.Search(query, language, limit, offset);
}
