using Microsoft.Data.Sqlite;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Storage;

/// <summary>议题 / 决议 / 候选三张表的读写。领域规则在 Core 的 <c>MeetingDomainService</c>。</summary>
public sealed partial class MeetingSession
{
    // ── 议题 ────────────────────────────────────────────────────────────

    public TopicRecord InsertTopic(TopicRecord topic)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mtg_topic (id, meeting_id, ordinal, title, summary, refs_json, created_at, updated_at)
            VALUES ($id, $meeting, $ord, $title, $summary, $refs, $created, $updated);
            """;
        cmd.Parameters.AddWithValue("$id", topic.Id);
        cmd.Parameters.AddWithValue("$meeting", topic.MeetingId);
        cmd.Parameters.AddWithValue("$ord", topic.Ordinal);
        cmd.Parameters.AddWithValue("$title", topic.Title);
        cmd.Parameters.AddWithValue("$summary", (object?)topic.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$refs", MeetingJson.Serialize(topic.Refs));
        cmd.Parameters.AddWithValue("$created", Iso(topic.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", Iso(topic.UpdatedAt));
        cmd.ExecuteNonQuery();
        return topic;
    }

    public IReadOnlyList<TopicRecord> ListTopics(string meetingId)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mtg_topic WHERE meeting_id = $m AND deleted_at IS NULL ORDER BY ordinal;";
        cmd.Parameters.AddWithValue("$m", meetingId);

        using var r = cmd.ExecuteReader();
        var list = new List<TopicRecord>();
        while (r.Read()) list.Add(ReadTopic(r));
        return list;
    }

    public TopicRecord? GetTopic(string id)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mtg_topic WHERE id = $id AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadTopic(r) : null;
    }

    public int NextTopicOrdinal(string meetingId)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(ordinal), -1) + 1 FROM mtg_topic WHERE meeting_id = $m AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("$m", meetingId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public void UpdateTopic(TopicRecord topic)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE mtg_topic SET ordinal = $ord, title = $title, summary = $summary,
                   refs_json = $refs, updated_at = $updated
             WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$ord", topic.Ordinal);
        cmd.Parameters.AddWithValue("$title", topic.Title);
        cmd.Parameters.AddWithValue("$summary", (object?)topic.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$refs", MeetingJson.Serialize(topic.Refs));
        cmd.Parameters.AddWithValue("$updated", Iso(topic.UpdatedAt));
        cmd.Parameters.AddWithValue("$id", topic.Id);
        cmd.ExecuteNonQuery();
    }

    public void SoftDeleteTopic(string id)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE mtg_topic SET deleted_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ── 决议 ────────────────────────────────────────────────────────────

    public DecisionRecord InsertDecision(DecisionRecord decision)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mtg_decision (id, meeting_id, topic_id, text, rationale, owner, status, anchor_ms, created_at, updated_at)
            VALUES ($id, $meeting, $topic, $text, $rationale, $owner, $status, $anchor, $created, $updated);
            """;
        cmd.Parameters.AddWithValue("$id", decision.Id);
        cmd.Parameters.AddWithValue("$meeting", decision.MeetingId);
        cmd.Parameters.AddWithValue("$topic", (object?)decision.TopicId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$text", decision.Text);
        cmd.Parameters.AddWithValue("$rationale", (object?)decision.Rationale ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$owner", (object?)decision.Owner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", decision.Status.ToString());
        cmd.Parameters.AddWithValue("$anchor", (object?)decision.AnchorMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", Iso(decision.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", Iso(decision.UpdatedAt));
        cmd.ExecuteNonQuery();
        return decision;
    }

    public IReadOnlyList<DecisionRecord> ListDecisions(string meetingId, DecisionStatus? status = null)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = status is null
            ? "SELECT * FROM mtg_decision WHERE meeting_id = $m AND deleted_at IS NULL ORDER BY created_at;"
            : "SELECT * FROM mtg_decision WHERE meeting_id = $m AND status = $s AND deleted_at IS NULL ORDER BY created_at;";
        cmd.Parameters.AddWithValue("$m", meetingId);
        if (status is { } s) cmd.Parameters.AddWithValue("$s", s.ToString());

        using var r = cmd.ExecuteReader();
        var list = new List<DecisionRecord>();
        while (r.Read()) list.Add(ReadDecision(r));
        return list;
    }

    public DecisionRecord? GetDecision(string id)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mtg_decision WHERE id = $id AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDecision(r) : null;
    }

    public void UpdateDecisionStatus(string id, DecisionStatus status)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE mtg_decision SET status = $s, updated_at = $updated WHERE id = $id;";
        cmd.Parameters.AddWithValue("$s", status.ToString());
        cmd.Parameters.AddWithValue("$updated", Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ── 待办候选 ────────────────────────────────────────────────────────

    public ActionRecord InsertAction(ActionRecord action)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mtg_action (id, meeting_id, topic_id, text, owner, due_at, status, planner_ref, anchor_ms, created_at, updated_at)
            VALUES ($id, $meeting, $topic, $text, $owner, $due, $status, $ref, $anchor, $created, $updated);
            """;
        cmd.Parameters.AddWithValue("$id", action.Id);
        cmd.Parameters.AddWithValue("$meeting", action.MeetingId);
        cmd.Parameters.AddWithValue("$topic", (object?)action.TopicId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$text", action.Text);
        cmd.Parameters.AddWithValue("$owner", (object?)action.Owner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$due", (object?)IsoOrNull(action.DueAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", action.Status.ToString());
        cmd.Parameters.AddWithValue("$ref", (object?)action.PlannerRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$anchor", (object?)action.AnchorMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", Iso(action.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", Iso(action.UpdatedAt));
        cmd.ExecuteNonQuery();
        return action;
    }

    public IReadOnlyList<ActionRecord> ListActions(string meetingId, ActionStatus? status = null)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = status is null
            ? "SELECT * FROM mtg_action WHERE meeting_id = $m AND deleted_at IS NULL ORDER BY created_at;"
            : "SELECT * FROM mtg_action WHERE meeting_id = $m AND status = $s AND deleted_at IS NULL ORDER BY created_at;";
        cmd.Parameters.AddWithValue("$m", meetingId);
        if (status is { } s) cmd.Parameters.AddWithValue("$s", s.ToString());

        using var r = cmd.ExecuteReader();
        var list = new List<ActionRecord>();
        while (r.Read()) list.Add(ReadAction(r));
        return list;
    }

    public ActionRecord? GetAction(string id)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mtg_action WHERE id = $id AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAction(r) : null;
    }

    public void UpdateActionStatus(string id, ActionStatus status, string? plannerRef = null)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE mtg_action SET status = $s, planner_ref = COALESCE($ref, planner_ref), updated_at = $updated
             WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$s", status.ToString());
        cmd.Parameters.AddWithValue("$ref", (object?)plannerRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ── 行映射 ──────────────────────────────────────────────────────────

    private static TopicRecord ReadTopic(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("meeting_id")),
        r.GetInt32(r.GetOrdinal("ordinal")),
        r.GetString(r.GetOrdinal("title")),
        ReadStringOrNull(r, "summary"),
        ParseRefs(ReadStringOrNull(r, "refs_json")),
        ParseIso(r.GetString(r.GetOrdinal("created_at"))),
        ParseIso(r.GetString(r.GetOrdinal("updated_at"))));

    private static DecisionRecord ReadDecision(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("meeting_id")),
        ReadStringOrNull(r, "topic_id"),
        r.GetString(r.GetOrdinal("text")),
        ReadStringOrNull(r, "rationale"),
        ReadStringOrNull(r, "owner"),
        ParseEnum<DecisionStatus>(r.GetString(r.GetOrdinal("status"))),
        r.IsDBNull(r.GetOrdinal("anchor_ms")) ? null : r.GetInt32(r.GetOrdinal("anchor_ms")),
        ParseIso(r.GetString(r.GetOrdinal("created_at"))),
        ParseIso(r.GetString(r.GetOrdinal("updated_at"))));

    private static ActionRecord ReadAction(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("meeting_id")),
        ReadStringOrNull(r, "topic_id"),
        r.GetString(r.GetOrdinal("text")),
        ReadStringOrNull(r, "owner"),
        ReadIsoOrNull(r, "due_at"),
        ParseEnum<ActionStatus>(r.GetString(r.GetOrdinal("status"))),
        ReadStringOrNull(r, "planner_ref"),
        r.IsDBNull(r.GetOrdinal("anchor_ms")) ? null : r.GetInt32(r.GetOrdinal("anchor_ms")),
        ParseIso(r.GetString(r.GetOrdinal("created_at"))),
        ParseIso(r.GetString(r.GetOrdinal("updated_at"))));

    private static IReadOnlyList<string> ParseRefs(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? Array.Empty<string>()
            : MeetingJson.Deserialize<string[]>(json) ?? Array.Empty<string>();
}
