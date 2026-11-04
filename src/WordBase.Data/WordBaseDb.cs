using Microsoft.Data.Sqlite;

namespace WordBase.Data;

/// <summary>SQLite 库文件与 schema 迁移。迁移用 PRAGMA user_version 递进，不依赖任何 ORM。</summary>
public sealed class WordBaseDb
{
    private readonly string _connectionString;

    public WordBaseDb(string databasePath)
    {
        DatabasePath = databasePath;
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        var version = ReadVersion(connection);
        if (version >= SchemaVersion)
        {
            return;
        }

        using var tx = connection.BeginTransaction();
        if (version < 1)
        {
            Exec(connection, tx, SchemaV1);
        }

        Exec(connection, tx, $"PRAGMA user_version={SchemaVersion};");
        tx.Commit();
    }

    public const int SchemaVersion = 1;

    private static int ReadVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private const string SchemaV1 = """
        CREATE TABLE entry (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            language      TEXT    NOT NULL,
            kind          INTEGER NOT NULL DEFAULT 0,
            term          TEXT    NOT NULL,
            term_norm     TEXT    NOT NULL,
            note          TEXT    NULL,
            source        INTEGER NOT NULL DEFAULT 0,
            hit_count     INTEGER NOT NULL DEFAULT 0,
            created_at    TEXT    NOT NULL,
            updated_at    TEXT    NOT NULL
        );
        CREATE UNIQUE INDEX ux_entry_norm ON entry(language, term_norm);

        CREATE TABLE reading (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            entry_id  INTEGER NOT NULL REFERENCES entry(id) ON DELETE CASCADE,
            text      TEXT    NOT NULL,
            is_common INTEGER NOT NULL DEFAULT 1,
            ordinal   INTEGER NOT NULL DEFAULT 0,
            note      TEXT    NULL
        );
        CREATE INDEX ix_reading_entry ON reading(entry_id);

        CREATE TABLE sense (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            entry_id    INTEGER NOT NULL REFERENCES entry(id) ON DELETE CASCADE,
            reading_id  INTEGER NULL REFERENCES reading(id) ON DELETE CASCADE,
            ordinal     INTEGER NOT NULL DEFAULT 0,
            gloss       TEXT    NOT NULL,
            note        TEXT    NULL,
            is_verified INTEGER NOT NULL DEFAULT 1
        );
        CREATE INDEX ix_sense_entry ON sense(entry_id);

        CREATE TABLE example (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            entry_id    INTEGER NOT NULL REFERENCES entry(id) ON DELETE CASCADE,
            reading_id  INTEGER NULL REFERENCES reading(id) ON DELETE CASCADE,
            sense_id    INTEGER NULL REFERENCES sense(id) ON DELETE SET NULL,
            pos         TEXT    NULL,
            text        TEXT    NOT NULL,
            translation TEXT    NULL,
            note        TEXT    NULL,
            source      INTEGER NOT NULL DEFAULT 0,
            is_verified INTEGER NOT NULL DEFAULT 1,
            ordinal     INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_example_entry ON example(entry_id);

        CREATE TABLE review_state (
            entry_id       INTEGER PRIMARY KEY REFERENCES entry(id) ON DELETE CASCADE,
            state          INTEGER NOT NULL DEFAULT 0,
            due_at         TEXT    NULL,
            stability      REAL    NOT NULL DEFAULT 0,
            difficulty     REAL    NOT NULL DEFAULT 0,
            reps           INTEGER NOT NULL DEFAULT 0,
            lapses         INTEGER NOT NULL DEFAULT 0,
            last_review_at TEXT    NULL,
            elapsed_days   INTEGER NOT NULL DEFAULT 0,
            scheduled_days INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_review_due ON review_state(due_at);

        CREATE TABLE review_log (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            entry_id        INTEGER NOT NULL REFERENCES entry(id) ON DELETE CASCADE,
            reviewed_at     TEXT    NOT NULL,
            phase           TEXT    NOT NULL,
            confidence      REAL    NULL,
            rating          INTEGER NOT NULL,
            prev_state      INTEGER NOT NULL,
            prev_stability  REAL    NOT NULL,
            prev_difficulty REAL    NOT NULL,
            next_stability  REAL    NOT NULL,
            next_difficulty REAL    NOT NULL,
            next_due_at     TEXT    NOT NULL,
            response_ms     INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_review_log_entry ON review_log(entry_id, reviewed_at);

        CREATE TABLE dup_event (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            entry_id INTEGER NOT NULL REFERENCES entry(id) ON DELETE CASCADE,
            seen_at  TEXT    NOT NULL,
            channel  INTEGER NOT NULL DEFAULT 0,
            query    TEXT    NULL
        );
        CREATE INDEX ix_dup_entry ON dup_event(entry_id);

        CREATE TABLE confusion_pair (
            entry_id       INTEGER NOT NULL REFERENCES entry(id) ON DELETE CASCADE,
            other_entry_id INTEGER NOT NULL REFERENCES entry(id) ON DELETE CASCADE,
            created_at     TEXT    NOT NULL,
            count          INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (entry_id, other_entry_id)
        );

        CREATE TABLE app_setting (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;
}
