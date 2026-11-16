using Microsoft.Data.Sqlite;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Storage;

/// <summary>KB 投影状态表。一场会议一行；重复投影只是更新这一行。</summary>
public sealed partial class MeetingSession
{
    public ProjectionRecord? GetProjection(string meetingId)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mtg_projection WHERE meeting_id = $m;";
        cmd.Parameters.AddWithValue("$m", meetingId);

        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadProjection(r) : null;
    }

    public ProjectionRecord UpsertProjection(ProjectionRecord record)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mtg_projection
                (meeting_id, status, doc_id, doc_version, block_count, include_transcript,
                 attempts, last_error, idempotency_key, updated_at)
            VALUES ($m, $status, $doc, $version, $blocks, $transcript, $attempts, $error, $key, $updated)
            ON CONFLICT(meeting_id) DO UPDATE SET
                status = excluded.status,
                doc_id = COALESCE(excluded.doc_id, mtg_projection.doc_id),
                doc_version = COALESCE(excluded.doc_version, mtg_projection.doc_version),
                block_count = excluded.block_count,
                include_transcript = excluded.include_transcript,
                attempts = excluded.attempts,
                last_error = excluded.last_error,
                idempotency_key = excluded.idempotency_key,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$m", record.MeetingId);
        cmd.Parameters.AddWithValue("$status", record.Status.ToString());
        cmd.Parameters.AddWithValue("$doc", (object?)record.DocId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$version", (object?)record.DocVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$blocks", record.BlockCount);
        cmd.Parameters.AddWithValue("$transcript", record.IncludeTranscript ? 1 : 0);
        cmd.Parameters.AddWithValue("$attempts", record.Attempts);
        cmd.Parameters.AddWithValue("$error", (object?)record.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$key", record.IdempotencyKey);
        cmd.Parameters.AddWithValue("$updated", Iso(record.UpdatedAt));
        cmd.ExecuteNonQuery();
        return record;
    }

    /// <summary>所有还没成功或用失败过的投影，给后台重试用。</summary>
    public IReadOnlyList<ProjectionRecord> ListPendingProjections(int limit = 50)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM mtg_projection
             WHERE status IN ('Pending', 'Failed')
             ORDER BY updated_at
             LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        using var r = cmd.ExecuteReader();
        var list = new List<ProjectionRecord>();
        while (r.Read()) list.Add(ReadProjection(r));
        return list;
    }

    private static ProjectionRecord ReadProjection(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("meeting_id")),
        ParseEnum<ProjectionStatus>(r.GetString(r.GetOrdinal("status"))),
        ReadStringOrNull(r, "doc_id"),
        r.IsDBNull(r.GetOrdinal("doc_version")) ? null : r.GetInt32(r.GetOrdinal("doc_version")),
        r.GetInt32(r.GetOrdinal("block_count")),
        r.GetInt32(r.GetOrdinal("include_transcript")) != 0,
        r.GetInt32(r.GetOrdinal("attempts")),
        ReadStringOrNull(r, "last_error"),
        r.GetString(r.GetOrdinal("idempotency_key")),
        ParseIso(r.GetString(r.GetOrdinal("updated_at"))));
}
