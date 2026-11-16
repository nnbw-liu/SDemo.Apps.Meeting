using Microsoft.Data.Sqlite;

namespace SDemo.App.Meeting.Storage;

/// <summary>
/// Schema 与迁移。规则：只增不改；每次结构变化 +1 <c>user_version</c>，老库就地升级。
/// </summary>
public static class MeetingSchema
{
    public const int CurrentVersion = 4;

    public static void Migrate(SqliteConnection conn)
    {
        var version = GetUserVersion(conn);

        if (version < 1)
        {
            using var tx = conn.BeginTransaction();
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS mtg_meeting (
                    id              TEXT PRIMARY KEY,
                    title           TEXT NOT NULL,
                    kind            TEXT NOT NULL,
                    start_at        TEXT NOT NULL,
                    end_at          TEXT,
                    status          TEXT NOT NULL,
                    current_run_id  TEXT,
                    created_at      TEXT NOT NULL,
                    updated_at      TEXT NOT NULL,
                    deleted_at      TEXT
                );

                CREATE TABLE IF NOT EXISTS mtg_audio_asset (
                    id           TEXT PRIMARY KEY,
                    meeting_id   TEXT NOT NULL REFERENCES mtg_meeting(id),
                    source       TEXT NOT NULL,
                    path         TEXT NOT NULL,
                    sample_rate  INTEGER NOT NULL,
                    channels     INTEGER NOT NULL,
                    bytes        INTEGER NOT NULL,
                    duration_ms  INTEGER NOT NULL,
                    sha256       TEXT,
                    created_at   TEXT NOT NULL,
                    deleted_at   TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_audio_meeting ON mtg_audio_asset(meeting_id);

                CREATE TABLE IF NOT EXISTS mtg_transcript_run (
                    id             TEXT PRIMARY KEY,
                    meeting_id     TEXT NOT NULL REFERENCES mtg_meeting(id),
                    engine         TEXT NOT NULL,
                    model          TEXT NOT NULL,
                    params_json    TEXT NOT NULL,
                    status         TEXT NOT NULL,
                    audio_asset_id TEXT REFERENCES mtg_audio_asset(id),
                    started_at     TEXT NOT NULL,
                    finished_at    TEXT,
                    error_code     TEXT,
                    error_message  TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_run_meeting ON mtg_transcript_run(meeting_id, started_at);

                CREATE TABLE IF NOT EXISTS mtg_segment (
                    id             TEXT PRIMARY KEY,
                    run_id         TEXT NOT NULL REFERENCES mtg_transcript_run(id) ON DELETE CASCADE,
                    ordinal        INTEGER NOT NULL,
                    start_ms       INTEGER NOT NULL,
                    end_ms         INTEGER NOT NULL,
                    text           TEXT NOT NULL,
                    confidence     REAL,
                    lang           TEXT,
                    speaker        TEXT,
                    speaker_source TEXT NOT NULL,
                    engine         TEXT,
                    warnings       TEXT
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_segment_run_ordinal ON mtg_segment(run_id, ordinal);

                CREATE TABLE IF NOT EXISTS mtg_meta (
                    k TEXT PRIMARY KEY,
                    v TEXT NOT NULL
                );
                """, tx);

            tx.Commit();
            SetUserVersion(conn, 1);
            version = 1;
        }

        if (version < 2)
        {
            using var tx = conn.BeginTransaction();
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS mtg_topic (
                    id          TEXT PRIMARY KEY,
                    meeting_id  TEXT NOT NULL REFERENCES mtg_meeting(id),
                    ordinal     INTEGER NOT NULL,
                    title       TEXT NOT NULL,
                    summary     TEXT,
                    refs_json   TEXT,
                    created_at  TEXT NOT NULL,
                    updated_at  TEXT NOT NULL,
                    deleted_at  TEXT
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_topic_meeting_ordinal
                    ON mtg_topic(meeting_id, ordinal) WHERE deleted_at IS NULL;

                CREATE TABLE IF NOT EXISTS mtg_decision (
                    id          TEXT PRIMARY KEY,
                    meeting_id  TEXT NOT NULL REFERENCES mtg_meeting(id),
                    topic_id    TEXT REFERENCES mtg_topic(id),
                    text        TEXT NOT NULL,
                    rationale   TEXT,
                    owner       TEXT,
                    status      TEXT NOT NULL,
                    anchor_ms   INTEGER,
                    created_at  TEXT NOT NULL,
                    updated_at  TEXT NOT NULL,
                    deleted_at  TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_decision_meeting ON mtg_decision(meeting_id, status);

                CREATE TABLE IF NOT EXISTS mtg_action (
                    id           TEXT PRIMARY KEY,
                    meeting_id   TEXT NOT NULL REFERENCES mtg_meeting(id),
                    topic_id     TEXT REFERENCES mtg_topic(id),
                    text         TEXT NOT NULL,
                    owner        TEXT,
                    due_at       TEXT,
                    status       TEXT NOT NULL,
                    planner_ref  TEXT,
                    anchor_ms    INTEGER,
                    created_at   TEXT NOT NULL,
                    updated_at   TEXT NOT NULL,
                    deleted_at   TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_action_meeting ON mtg_action(meeting_id, status);
                """, tx);

            tx.Commit();
            SetUserVersion(conn, 2);
            version = 2;
        }

        if (version < 3)
        {
            using var tx = conn.BeginTransaction();
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS mtg_audit (
                    id           TEXT PRIMARY KEY,
                    at           TEXT NOT NULL,
                    actor        TEXT NOT NULL,
                    tool         TEXT NOT NULL,
                    action       TEXT NOT NULL,
                    entity_type  TEXT NOT NULL,
                    entity_id    TEXT,
                    meeting_id   TEXT,
                    args_summary TEXT,
                    outcome      TEXT NOT NULL,
                    error        TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_audit_meeting ON mtg_audit(meeting_id, at);
                CREATE INDEX IF NOT EXISTS ix_audit_at ON mtg_audit(at);
                """, tx);

            tx.Commit();
            SetUserVersion(conn, 3);
            version = 3;
        }

        if (version < 4)
        {
            using var tx = conn.BeginTransaction();
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS mtg_projection (
                    meeting_id         TEXT PRIMARY KEY REFERENCES mtg_meeting(id),
                    status             TEXT NOT NULL,
                    doc_id             TEXT,
                    doc_version        INTEGER,
                    block_count        INTEGER NOT NULL DEFAULT 0,
                    include_transcript INTEGER NOT NULL DEFAULT 0,
                    attempts           INTEGER NOT NULL DEFAULT 0,
                    last_error         TEXT,
                    idempotency_key    TEXT NOT NULL,
                    updated_at         TEXT NOT NULL
                );
                """, tx);

            tx.Commit();
            SetUserVersion(conn, 4);
            version = 4;
        }

        if (version < CurrentVersion)
            throw new InvalidOperationException($"schema 版本 {version} 无法升级到 {CurrentVersion}");
    }

    public static int GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static void SetUserVersion(SqliteConnection conn, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static IReadOnlyList<string> ListTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'mtg_%' ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
