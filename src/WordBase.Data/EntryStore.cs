using Microsoft.Data.Sqlite;
using WordBase.Core.Abstractions;
using WordBase.Core.Models;
using WordBase.Core.Text;

namespace WordBase.Data;

public sealed class EntryStore : IEntryStore
{
    private readonly WordBaseDb _db;

    public EntryStore(WordBaseDb db) => _db = db;

    public Entry? FindByTerm(string language, string termNormalized)
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM entry WHERE language=$lang AND term_norm=$norm LIMIT 1;";
        cmd.Parameters.AddWithValue("$lang", language);
        cmd.Parameters.AddWithValue("$norm", termNormalized);
        var id = cmd.ExecuteScalar();
        return id is null or DBNull ? null : Get(Convert.ToInt64(id));
    }

    public Entry? Get(long id)
    {
        using var connection = _db.Open();

        Entry entry;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, language, kind, term, term_norm, note, source, hit_count, created_at, updated_at
                FROM entry WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            entry = new Entry
            {
                Id = reader.GetInt64(0),
                Language = reader.GetString(1),
                Kind = (EntryKind)reader.GetInt32(2),
                Term = reader.GetString(3),
                TermNormalized = reader.GetString(4),
                Note = reader.GetNullable<string>("note"),
                Source = (EntrySource)reader.GetInt32(6),
                HitCount = reader.GetInt32(7),
                CreatedAt = Sql.ToDate(reader.GetString(8)) ?? DateTimeOffset.MinValue,
                UpdatedAt = Sql.ToDate(reader.GetString(9)) ?? DateTimeOffset.MinValue,
            };
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, entry_id, text, is_common, ordinal, note FROM reading WHERE entry_id=$id ORDER BY ordinal, id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                entry.Readings.Add(new Reading
                {
                    Id = reader.GetInt64(0),
                    EntryId = reader.GetInt64(1),
                    Text = reader.GetString(2),
                    IsCommon = reader.GetInt32(3) != 0,
                    Ordinal = reader.GetInt32(4),
                    Note = reader.GetNullable<string>("note"),
                });
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, entry_id, reading_id, ordinal, gloss, note, is_verified FROM sense WHERE entry_id=$id ORDER BY ordinal, id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                entry.Senses.Add(new Sense
                {
                    Id = reader.GetInt64(0),
                    EntryId = reader.GetInt64(1),
                    ReadingId = reader.GetNullableLong("reading_id"),
                    Ordinal = reader.GetInt32(3),
                    Gloss = reader.GetString(4),
                    Note = reader.GetNullable<string>("note"),
                    IsVerified = reader.GetInt32(6) != 0,
                });
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, entry_id, reading_id, sense_id, pos, text, translation, note, source, is_verified, ordinal
                FROM example WHERE entry_id=$id ORDER BY ordinal, id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                entry.Examples.Add(new Example
                {
                    Id = reader.GetInt64(0),
                    EntryId = reader.GetInt64(1),
                    ReadingId = reader.GetNullableLong("reading_id"),
                    SenseId = reader.GetNullableLong("sense_id"),
                    PartOfSpeech = reader.GetNullable<string>("pos"),
                    Text = reader.GetString(5),
                    Translation = reader.GetNullable<string>("translation"),
                    Note = reader.GetNullable<string>("note"),
                    Source = (EntrySource)reader.GetInt32(8),
                    IsVerified = reader.GetInt32(9) != 0,
                    Ordinal = reader.GetInt32(10),
                });
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT entry_id, state, due_at, stability, difficulty, reps, lapses, last_review_at,
                       elapsed_days, scheduled_days
                FROM review_state WHERE entry_id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                entry.Review = new ReviewState
                {
                    EntryId = reader.GetInt64(0),
                    State = (CardState)reader.GetInt32(1),
                    DueAt = Sql.ToDate(reader.GetNullable<string>("due_at")),
                    Stability = reader.GetDouble(3),
                    Difficulty = reader.GetDouble(4),
                    Reps = reader.GetInt32(5),
                    Lapses = reader.GetInt32(6),
                    LastReviewAt = Sql.ToDate(reader.GetNullable<string>("last_review_at")),
                    ElapsedDays = reader.GetInt32(8),
                    ScheduledDays = reader.GetInt32(9),
                };
            }
        }

        return entry;
    }

    public IReadOnlyList<EntrySummary> Search(string? query, string? language, int limit = 200, int offset = 0)
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();

        var normalized = TermNormalizer.Normalize(query);
        cmd.CommandText = """
            SELECT e.id, e.language, e.kind, e.term,
                   COALESCE((SELECT GROUP_CONCAT(text, ' / ') FROM reading r WHERE r.entry_id = e.id), '') AS readings,
                   COALESCE((SELECT gloss FROM sense s WHERE s.entry_id = e.id ORDER BY s.ordinal, s.id LIMIT 1), '') AS gloss,
                   (SELECT COUNT(*) FROM example x WHERE x.entry_id = e.id) AS ex_count,
                   rs.due_at, COALESCE(rs.state, 0)
            FROM entry e
            LEFT JOIN review_state rs ON rs.entry_id = e.id
            WHERE ($q = '' OR e.term_norm LIKE $like
                   OR EXISTS (SELECT 1 FROM sense s WHERE s.entry_id = e.id AND LOWER(s.gloss) LIKE $like)
                   OR EXISTS (SELECT 1 FROM reading r WHERE r.entry_id = e.id AND LOWER(r.text) LIKE $like))
              AND ($lang = '' OR e.language = $lang)
            ORDER BY e.updated_at DESC, e.id DESC
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$q", normalized);
        cmd.Parameters.AddWithValue("$like", $"%{normalized}%");
        cmd.Parameters.AddWithValue("$lang", language ?? string.Empty);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var results = new List<EntrySummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new EntrySummary(
                reader.GetInt64(0),
                reader.GetString(1),
                (EntryKind)reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                Sql.ToDate(reader.GetNullable<string>("due_at")),
                (CardState)reader.GetInt32(8)));
        }

        return results;
    }

    public int CountAll() => ScalarInt("SELECT COUNT(*) FROM entry;", null);

    public int CountByLanguage(string language) =>
        ScalarInt("SELECT COUNT(*) FROM entry WHERE language=$lang;", cmd => cmd.Parameters.AddWithValue("$lang", language));

    public long Create(Entry entry)
    {
        EnsureCategory(entry);
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();

        var id = InsertEntryRow(connection, tx, entry);
        WriteChildren(connection, tx, entry, id);

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO review_state(entry_id, state, due_at, stability, difficulty, reps, lapses,
                                         last_review_at, elapsed_days, scheduled_days)
                VALUES($id, $state, $due, $stab, $diff, $reps, $lapses, $last, $elapsed, $scheduled);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$state", (int)entry.Review.State);
            cmd.Parameters.AddWithValue("$due", (object?)Sql.ToIso(entry.Review.DueAt ?? entry.CreatedAt) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$stab", entry.Review.Stability);
            cmd.Parameters.AddWithValue("$diff", entry.Review.Difficulty);
            cmd.Parameters.AddWithValue("$reps", entry.Review.Reps);
            cmd.Parameters.AddWithValue("$lapses", entry.Review.Lapses);
            cmd.Parameters.AddWithValue("$last", (object?)Sql.ToIso(entry.Review.LastReviewAt) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$elapsed", entry.Review.ElapsedDays);
            cmd.Parameters.AddWithValue("$scheduled", entry.Review.ScheduledDays);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        entry.Id = id;
        return id;
    }

    public void Update(Entry entry)
    {
        EnsureCategory(entry);
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE entry SET language=$lang, kind=$kind, term=$term, term_norm=$norm, note=$note,
                                 source=$source, updated_at=$updated
                WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$lang", entry.Language);
            cmd.Parameters.AddWithValue("$kind", (int)entry.Kind);
            cmd.Parameters.AddWithValue("$term", entry.Term);
            cmd.Parameters.AddWithValue("$norm", entry.TermNormalized);
            cmd.Parameters.AddWithValue("$note", (object?)entry.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", (int)entry.Source);
            cmd.Parameters.AddWithValue("$updated", Sql.ToIso(entry.UpdatedAt));
            cmd.Parameters.AddWithValue("$id", entry.Id);
            cmd.ExecuteNonQuery();
        }

        foreach (var table in new[] { "example", "sense", "reading" })
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE entry_id=$id;";
            cmd.Parameters.AddWithValue("$id", entry.Id);
            cmd.ExecuteNonQuery();
        }

        WriteChildren(connection, tx, entry, entry.Id);
        tx.Commit();
    }

    public void Delete(long id)
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM entry WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void RecordEncounter(long entryId, DuplicateChannel channel, string? query, DateTimeOffset now)
    {
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO dup_event(entry_id, seen_at, channel, query) VALUES($id, $at, $ch, $q);";
            cmd.Parameters.AddWithValue("$id", entryId);
            cmd.Parameters.AddWithValue("$at", Sql.ToIso(now));
            cmd.Parameters.AddWithValue("$ch", (int)channel);
            cmd.Parameters.AddWithValue("$q", (object?)query ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE entry SET hit_count = hit_count + 1 WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", entryId);
            cmd.ExecuteNonQuery();
        }

        // 强化：把到期时间按剩余时长缩短 20%，但不早于现在。重复遇见越多，复习越靠前。
        DateTimeOffset? due = null;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT due_at FROM review_state WHERE entry_id=$id;";
            cmd.Parameters.AddWithValue("$id", entryId);
            var raw = cmd.ExecuteScalar();
            if (raw is string text)
            {
                due = Sql.ToDate(text);
            }
        }

        if (due is { } current && current > now)
        {
            var remaining = current - now;
            var boosted = now + TimeSpan.FromTicks((long)(remaining.Ticks * 0.8));
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE review_state SET due_at=$due WHERE entry_id=$id;";
            cmd.Parameters.AddWithValue("$due", Sql.ToIso(boosted));
            cmd.Parameters.AddWithValue("$id", entryId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<Entry> GetDue(DateTimeOffset now, int limit)
    {
        var ids = new List<long>();
        using (var connection = _db.Open())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT entry_id FROM review_state
                WHERE due_at IS NOT NULL AND due_at <= $now
                ORDER BY due_at
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$now", Sql.ToIso(now));
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        return ids.Select(Get).Where(e => e is not null).Select(e => e!).ToList();
    }

    public int CountDue(DateTimeOffset now) =>
        ScalarInt("SELECT COUNT(*) FROM review_state WHERE due_at IS NOT NULL AND due_at <= $now;",
            cmd => cmd.Parameters.AddWithValue("$now", Sql.ToIso(now)));

    public IReadOnlyList<string> GetDistractorGlosses(string language, long excludeEntryId, int limit)
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.gloss FROM sense s
            JOIN entry e ON e.id = s.entry_id
            WHERE e.language = $lang AND e.id <> $id AND TRIM(s.gloss) <> ''
            ORDER BY RANDOM()
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$lang", language);
        cmd.Parameters.AddWithValue("$id", excludeEntryId);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    public void SaveReview(ReviewState state, ReviewLog log)
    {
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO review_state(entry_id, state, due_at, stability, difficulty, reps, lapses,
                                         last_review_at, elapsed_days, scheduled_days)
                VALUES($id, $state, $due, $stab, $diff, $reps, $lapses, $last, $elapsed, $scheduled)
                ON CONFLICT(entry_id) DO UPDATE SET
                    state=excluded.state, due_at=excluded.due_at, stability=excluded.stability,
                    difficulty=excluded.difficulty, reps=excluded.reps, lapses=excluded.lapses,
                    last_review_at=excluded.last_review_at, elapsed_days=excluded.elapsed_days,
                    scheduled_days=excluded.scheduled_days;
                """;
            cmd.Parameters.AddWithValue("$id", state.EntryId);
            cmd.Parameters.AddWithValue("$state", (int)state.State);
            cmd.Parameters.AddWithValue("$due", (object?)Sql.ToIso(state.DueAt) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$stab", state.Stability);
            cmd.Parameters.AddWithValue("$diff", state.Difficulty);
            cmd.Parameters.AddWithValue("$reps", state.Reps);
            cmd.Parameters.AddWithValue("$lapses", state.Lapses);
            cmd.Parameters.AddWithValue("$last", (object?)Sql.ToIso(state.LastReviewAt) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$elapsed", state.ElapsedDays);
            cmd.Parameters.AddWithValue("$scheduled", state.ScheduledDays);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO review_log(entry_id, reviewed_at, phase, confidence, rating, prev_state,
                                       prev_stability, prev_difficulty, next_stability, next_difficulty,
                                       next_due_at, response_ms)
                VALUES($id, $at, $phase, $conf, $rating, $pstate, $pstab, $pdiff, $nstab, $ndiff, $ndue, $ms);
                """;
            cmd.Parameters.AddWithValue("$id", log.EntryId);
            cmd.Parameters.AddWithValue("$at", Sql.ToIso(log.ReviewedAt));
            cmd.Parameters.AddWithValue("$phase", log.Phase);
            cmd.Parameters.AddWithValue("$conf", (object?)log.Confidence ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rating", (int)log.Rating);
            cmd.Parameters.AddWithValue("$pstate", (int)log.PrevState);
            cmd.Parameters.AddWithValue("$pstab", log.PrevStability);
            cmd.Parameters.AddWithValue("$pdiff", log.PrevDifficulty);
            cmd.Parameters.AddWithValue("$nstab", log.NextStability);
            cmd.Parameters.AddWithValue("$ndiff", log.NextDifficulty);
            cmd.Parameters.AddWithValue("$ndue", Sql.ToIso(log.NextDueAt));
            cmd.Parameters.AddWithValue("$ms", log.ResponseMs);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void RecordConfusion(long entryId, long otherEntryId, DateTimeOffset now)
    {
        if (entryId == otherEntryId)
        {
            return;
        }

        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO confusion_pair(entry_id, other_entry_id, created_at, count)
            VALUES($id, $other, $at, 1)
            ON CONFLICT(entry_id, other_entry_id) DO UPDATE SET count = count + 1;
            """;
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.Parameters.AddWithValue("$other", otherEntryId);
        cmd.Parameters.AddWithValue("$at", Sql.ToIso(now));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<long> GetConfusionPartnerIds(long entryId)
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT other_entry_id FROM confusion_pair WHERE entry_id=$id ORDER BY count DESC;";
        cmd.Parameters.AddWithValue("$id", entryId);
        var list = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetInt64(0));
        }

        return list;
    }

    public EntryStats GetStats(DateTimeOffset now)
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM entry),
                (SELECT COUNT(*) FROM review_state WHERE due_at IS NOT NULL AND due_at <= $now),
                (SELECT COUNT(*) FROM review_state WHERE state = 0),
                (SELECT COUNT(*) FROM review_state WHERE state = 1),
                (SELECT COUNT(*) FROM review_state WHERE state = 2),
                (SELECT COUNT(*) FROM review_state WHERE state = 3);
            """;
        cmd.Parameters.AddWithValue("$now", Sql.ToIso(now));
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new EntryStats(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt32(5))
            : new EntryStats(0, 0, 0, 0, 0, 0);
    }

    private static long InsertEntryRow(SqliteConnection connection, SqliteTransaction tx, Entry entry)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO entry(language, kind, term, term_norm, note, source, hit_count, created_at, updated_at)
            VALUES($lang, $kind, $term, $norm, $note, $source, $hits, $created, $updated);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$lang", entry.Language);
        cmd.Parameters.AddWithValue("$kind", (int)entry.Kind);
        cmd.Parameters.AddWithValue("$term", entry.Term);
        cmd.Parameters.AddWithValue("$norm", entry.TermNormalized);
        cmd.Parameters.AddWithValue("$note", (object?)entry.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", (int)entry.Source);
        cmd.Parameters.AddWithValue("$hits", entry.HitCount);
        cmd.Parameters.AddWithValue("$created", Sql.ToIso(entry.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", Sql.ToIso(entry.UpdatedAt));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 写入读音/释义/例句。草稿里的子实体用临时 id（负数），这里统一换成数据库 id，
    /// 并把例句上的引用一起改掉，避免出现悬空外键。
    /// </summary>
    private static void WriteChildren(SqliteConnection connection, SqliteTransaction tx, Entry entry, long entryId)
    {
        var readingMap = new Dictionary<long, long>();
        var senseMap = new Dictionary<long, long>();
        var orderedReadings = entry.Readings
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .OrderBy(r => r.Ordinal)
            .ToList();

        for (var index = 0; index < orderedReadings.Count; index++)
        {
            var reading = orderedReadings[index];
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO reading(entry_id, text, is_common, ordinal, note)
                VALUES($eid, $text, $common, $ordinal, $note);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$eid", entryId);
            cmd.Parameters.AddWithValue("$text", TermNormalizer.Tidy(reading.Text));
            cmd.Parameters.AddWithValue("$common", reading.IsCommon ? 1 : 0);
            cmd.Parameters.AddWithValue("$ordinal", index);
            cmd.Parameters.AddWithValue("$note", (object?)reading.Note ?? DBNull.Value);
            var newId = Convert.ToInt64(cmd.ExecuteScalar());
            readingMap[reading.Id] = newId;
            reading.Id = newId;
        }

        var orderedSenses = entry.Senses
            .Where(s => !string.IsNullOrWhiteSpace(s.Gloss))
            .OrderBy(s => s.Ordinal)
            .ToList();

        for (var index = 0; index < orderedSenses.Count; index++)
        {
            var sense = orderedSenses[index];
            long? readingId = sense.ReadingId is { } rid && readingMap.TryGetValue(rid, out var mapped) ? mapped : null;

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sense(entry_id, reading_id, ordinal, gloss, note, is_verified)
                VALUES($eid, $rid, $ordinal, $gloss, $note, $verified);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$eid", entryId);
            cmd.Parameters.AddWithValue("$rid", (object?)readingId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ordinal", index);
            cmd.Parameters.AddWithValue("$gloss", sense.Gloss.Trim());
            cmd.Parameters.AddWithValue("$note", (object?)sense.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$verified", sense.IsVerified ? 1 : 0);
            var newId = Convert.ToInt64(cmd.ExecuteScalar());
            senseMap[sense.Id] = newId;
            sense.Id = newId;
        }

        var orderedExamples = entry.Examples
            .Where(e => !string.IsNullOrWhiteSpace(e.Text))
            .OrderBy(e => e.Ordinal)
            .ToList();

        for (var index = 0; index < orderedExamples.Count; index++)
        {
            var example = orderedExamples[index];
            long? readingId = example.ReadingId is { } rid && readingMap.TryGetValue(rid, out var mappedReading)
                ? mappedReading
                : null;
            long? senseId = example.SenseId is { } sid && senseMap.TryGetValue(sid, out var mappedSense)
                ? mappedSense
                : null;

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO example(entry_id, reading_id, sense_id, pos, text, translation, note, source,
                                    is_verified, ordinal)
                VALUES($eid, $rid, $sid, $pos, $text, $translation, $note, $source, $verified, $ordinal);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$eid", entryId);
            cmd.Parameters.AddWithValue("$rid", (object?)readingId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sid", (object?)senseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pos", string.IsNullOrWhiteSpace(example.PartOfSpeech)
                ? DBNull.Value
                : (object)example.PartOfSpeech!.Trim());
            cmd.Parameters.AddWithValue("$text", example.Text.Trim());
            cmd.Parameters.AddWithValue("$translation", (object?)example.Translation?.Trim() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$note", (object?)example.Note?.Trim() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", (int)example.Source);
            cmd.Parameters.AddWithValue("$verified", example.IsVerified ? 1 : 0);
            cmd.Parameters.AddWithValue("$ordinal", index);
            example.Id = Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    private int ScalarInt(string sql, Action<SqliteCommand>? bind)
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>词形与语言字段在写库前兜底，避免绕过服务层时写入空检索键。</summary>
    private static void EnsureCategory(Entry entry)
    {
        entry.Term = TermNormalizer.Tidy(entry.Term);
        if (string.IsNullOrWhiteSpace(entry.TermNormalized))
        {
            entry.TermNormalized = TermNormalizer.Normalize(entry.Term);
        }

        if (string.IsNullOrWhiteSpace(entry.Language))
        {
            entry.Language = "en";
        }
        else
        {
            entry.Language = entry.Language.Trim().ToLowerInvariant();
        }
    }
}
