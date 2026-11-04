using WordBase.Core.Models;

namespace WordBase.Core.Abstractions;

public interface IEntryStore
{
    Entry? FindByTerm(string language, string termNormalized);

    Entry? Get(long id);

    IReadOnlyList<EntrySummary> Search(string? query, string? language, int limit = 200, int offset = 0);

    int CountAll();

    int CountByLanguage(string language);

    long Create(Entry entry);

    void Update(Entry entry);

    void Delete(long id);

    /// <summary>记录一次「重复遇见」：写 dup_event、命中计数 +1，并把到期时间小幅提前。</summary>
    void RecordEncounter(long entryId, DuplicateChannel channel, string? query, DateTimeOffset now);

    IReadOnlyList<Entry> GetDue(DateTimeOffset now, int limit);

    int CountDue(DateTimeOffset now);

    IReadOnlyList<string> GetDistractorGlosses(string language, long excludeEntryId, int limit);

    void SaveReview(ReviewState state, ReviewLog log);

    void RecordConfusion(long entryId, long otherEntryId, DateTimeOffset now);

    IReadOnlyList<long> GetConfusionPartnerIds(long entryId);

    EntryStats GetStats(DateTimeOffset now);
}

public sealed record EntryStats(int Total, int Due, int New, int Learning, int Review, int Relearning);

public interface ISettingsStore
{
    string? Get(string key);

    void Set(string key, string value);

    IReadOnlyDictionary<string, string> GetAll();
}

public interface ISecretStore
{
    string? Read(string name);

    void Write(string name, string? value);
}
