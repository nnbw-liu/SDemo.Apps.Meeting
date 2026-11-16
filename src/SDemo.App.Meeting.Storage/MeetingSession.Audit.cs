using Microsoft.Data.Sqlite;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Storage;

/// <summary>审计表读写。不做软删——审计是事实，不回收。</summary>
public sealed partial class MeetingSession
{
    public AuditRecord InsertAudit(AuditRecord record)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mtg_audit (id, at, actor, tool, action, entity_type, entity_id, meeting_id, args_summary, outcome, error)
            VALUES ($id, $at, $actor, $tool, $action, $type, $entity, $meeting, $args, $outcome, $error);
            """;
        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.Parameters.AddWithValue("$at", Iso(record.At));
        cmd.Parameters.AddWithValue("$actor", record.Actor.ToString());
        cmd.Parameters.AddWithValue("$tool", record.Tool);
        cmd.Parameters.AddWithValue("$action", record.Action);
        cmd.Parameters.AddWithValue("$type", record.EntityType);
        cmd.Parameters.AddWithValue("$entity", (object?)record.EntityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$meeting", (object?)record.MeetingId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$args", (object?)record.ArgsSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome", record.Outcome);
        cmd.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return record;
    }

    public IReadOnlyList<AuditRecord> ListAudit(string? meetingId = null, int limit = 100)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = meetingId is null
            ? "SELECT * FROM mtg_audit ORDER BY at DESC LIMIT $limit;"
            : "SELECT * FROM mtg_audit WHERE meeting_id = $m ORDER BY at DESC LIMIT $limit;";
        if (meetingId is not null) cmd.Parameters.AddWithValue("$m", meetingId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        using var r = cmd.ExecuteReader();
        var list = new List<AuditRecord>();
        while (r.Read())
        {
            list.Add(new AuditRecord(
                r.GetString(r.GetOrdinal("id")),
                ParseIso(r.GetString(r.GetOrdinal("at"))),
                ParseEnum<AuditActor>(r.GetString(r.GetOrdinal("actor"))),
                r.GetString(r.GetOrdinal("tool")),
                r.GetString(r.GetOrdinal("action")),
                r.GetString(r.GetOrdinal("entity_type")),
                ReadStringOrNull(r, "entity_id"),
                ReadStringOrNull(r, "meeting_id"),
                ReadStringOrNull(r, "args_summary"),
                r.GetString(r.GetOrdinal("outcome")),
                ReadStringOrNull(r, "error")));
        }

        return list;
    }
}
