using Microsoft.Data.Sqlite;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Storage;

/// <summary>分析队列。claim 用带状态条件的 UPDATE，保证两个 Agent 不会抢同一场会议。</summary>
public sealed partial class MeetingSession
{
    public AnalysisJob InsertAnalysis(AnalysisJob job)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mtg_analysis
                (id, meeting_id, status, requested_by, prompt_version, provider,
                 segments_total, segments_done, topics_created, decisions_created, actions_created,
                 error, created_at, started_at, finished_at)
            VALUES ($id, $meeting, $status, $by, $prompt, $provider,
                    $total, 0, 0, 0, 0, NULL, $created, NULL, NULL);
            """;
        cmd.Parameters.AddWithValue("$id", job.Id);
        cmd.Parameters.AddWithValue("$meeting", job.MeetingId);
        cmd.Parameters.AddWithValue("$status", job.Status.ToString());
        cmd.Parameters.AddWithValue("$by", job.RequestedBy);
        cmd.Parameters.AddWithValue("$prompt", (object?)job.PromptVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$provider", (object?)job.Provider ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$total", job.SegmentsTotal);
        cmd.Parameters.AddWithValue("$created", Iso(job.CreatedAt));
        cmd.ExecuteNonQuery();
        return job;
    }

    public AnalysisJob? GetAnalysis(string id)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mtg_analysis WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAnalysis(r) : null;
    }

    /// <summary>一场会议最近一次分析（用于界面显示与去重入队）。</summary>
    public AnalysisJob? GetLatestAnalysis(string meetingId)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mtg_analysis WHERE meeting_id = $m ORDER BY created_at DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$m", meetingId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAnalysis(r) : null;
    }

    /// <summary>取一个排队中的作业并原子置为 Running。没有就返回 null。</summary>
    public AnalysisJob? ClaimNextAnalysis(DateTimeOffset now)
    {
        using var conn = _db.Connect();

        string? id;
        using (var pick = conn.CreateCommand())
        {
            pick.CommandText = "SELECT id FROM mtg_analysis WHERE status = 'Queued' ORDER BY created_at LIMIT 1;";
            id = pick.ExecuteScalar() as string;
        }

        if (id is null) return null;

        using (var claim = conn.CreateCommand())
        {
            claim.CommandText = """
                UPDATE mtg_analysis SET status = 'Running', started_at = $now
                 WHERE id = $id AND status = 'Queued';
                """;
            claim.Parameters.AddWithValue("$now", Iso(now));
            claim.Parameters.AddWithValue("$id", id);
            if (claim.ExecuteNonQuery() == 0) return null;   // 被别的进程抢走了
        }

        return GetAnalysis(id);
    }

    public void UpdateAnalysisProgress(string id, int segmentsDone)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE mtg_analysis SET segments_done = $done WHERE id = $id;";
        cmd.Parameters.AddWithValue("$done", segmentsDone);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public AnalysisJob? CompleteAnalysis(
        string id,
        int topicsCreated,
        int decisionsCreated,
        int actionsCreated,
        string? provider,
        DateTimeOffset now)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE mtg_analysis SET status = 'Done', finished_at = $now,
                   topics_created = $t, decisions_created = $d, actions_created = $a,
                   provider = COALESCE($provider, provider), segments_done = segments_total
             WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$now", Iso(now));
        cmd.Parameters.AddWithValue("$t", topicsCreated);
        cmd.Parameters.AddWithValue("$d", decisionsCreated);
        cmd.Parameters.AddWithValue("$a", actionsCreated);
        cmd.Parameters.AddWithValue("$provider", (object?)provider ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return GetAnalysis(id);
    }

    public AnalysisJob? FailAnalysis(string id, string error, DateTimeOffset now)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE mtg_analysis SET status = 'Failed', error = $error, finished_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$error", error.Length <= 500 ? error : error[..500]);
        cmd.Parameters.AddWithValue("$now", Iso(now));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return GetAnalysis(id);
    }

    /// <summary>进程重启时把卡在 Running 的作业标成失败，否则它们永远占着（Agent 崩了不会再回来）。</summary>
    public int MarkRunningAnalysisInterrupted(string reason, DateTimeOffset now)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE mtg_analysis SET status = 'Failed', error = $error, finished_at = $now
             WHERE status = 'Running';
            """;
        cmd.Parameters.AddWithValue("$error", reason);
        cmd.Parameters.AddWithValue("$now", Iso(now));
        return cmd.ExecuteNonQuery();
    }

    private static AnalysisJob ReadAnalysis(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("meeting_id")),
        ParseEnum<AnalysisJobStatus>(r.GetString(r.GetOrdinal("status"))),
        r.GetString(r.GetOrdinal("requested_by")),
        ReadStringOrNull(r, "prompt_version"),
        ReadStringOrNull(r, "provider"),
        r.GetInt32(r.GetOrdinal("segments_total")),
        r.GetInt32(r.GetOrdinal("segments_done")),
        r.GetInt32(r.GetOrdinal("topics_created")),
        r.GetInt32(r.GetOrdinal("decisions_created")),
        r.GetInt32(r.GetOrdinal("actions_created")),
        ReadStringOrNull(r, "error"),
        ParseIso(r.GetString(r.GetOrdinal("created_at"))),
        r.IsDBNull(r.GetOrdinal("started_at")) ? null : ParseIso(r.GetString(r.GetOrdinal("started_at"))),
        r.IsDBNull(r.GetOrdinal("finished_at")) ? null : ParseIso(r.GetString(r.GetOrdinal("finished_at"))));
}
