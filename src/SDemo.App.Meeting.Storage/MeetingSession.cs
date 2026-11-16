using Microsoft.Data.Sqlite;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Storage;

/// <summary>
/// 表读写。这里是唯一允许出现 SQL 的地方；领域规则在 Core。
/// 所有枚举以 PascalCase 文本落库，读回时忽略大小写解析。
/// </summary>
public sealed class MeetingSession
{
    private readonly MeetingDatabase _db;

    public MeetingSession(MeetingDatabase db) => _db = db;

    public MeetingDatabase Database => _db;

    // ── 会议 ────────────────────────────────────────────────────────────

    public MeetingRecord InsertMeeting(MeetingRecord meeting)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mtg_meeting (id, title, kind, start_at, end_at, status, current_run_id, created_at, updated_at)
            VALUES ($id, $title, $kind, $start, $end, $status, $run, $created, $updated);
            """;
        cmd.Parameters.AddWithValue("$id", meeting.Id);
        cmd.Parameters.AddWithValue("$title", meeting.Title);
        cmd.Parameters.AddWithValue("$kind", meeting.Kind.ToString());
        cmd.Parameters.AddWithValue("$start", Iso(meeting.StartAt));
        cmd.Parameters.AddWithValue("$end", (object?)IsoOrNull(meeting.EndAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", meeting.Status.ToString());
        cmd.Parameters.AddWithValue("$run", (object?)meeting.CurrentRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", Iso(meeting.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", Iso(meeting.UpdatedAt));
        cmd.ExecuteNonQuery();
        return meeting;
    }

    public MeetingRecord? GetMeeting(string id)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mtg_meeting WHERE id = $id AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadMeeting(r) : null;
    }

    public IReadOnlyList<MeetingRecord> ListMeetings(int limit = 50, bool includeDeleted = false)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT * FROM mtg_meeting
            {(includeDeleted ? "" : "WHERE deleted_at IS NULL")}
            ORDER BY start_at DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        using var r = cmd.ExecuteReader();
        var list = new List<MeetingRecord>();
        while (r.Read()) list.Add(ReadMeeting(r));
        return list;
    }

    public void UpdateMeetingStatus(string meetingId, MeetingStatus status, string? currentRunId = null)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE mtg_meeting
               SET status = $status,
                   current_run_id = COALESCE($run, current_run_id),
                   updated_at = $updated
             WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$status", status.ToString());
        cmd.Parameters.AddWithValue("$run", (object?)currentRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", meetingId);
        cmd.ExecuteNonQuery();
    }

    public void SetCurrentRun(string meetingId, string runId)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE mtg_meeting SET current_run_id = $run, updated_at = $updated WHERE id = $id;";
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$updated", Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", meetingId);
        cmd.ExecuteNonQuery();
    }

    public void SoftDeleteMeeting(string meetingId)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE mtg_meeting SET deleted_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", meetingId);
        cmd.ExecuteNonQuery();
    }

    // ── 音频 ────────────────────────────────────────────────────────────

    public AudioAssetRecord InsertAudio(AudioAssetRecord asset)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mtg_audio_asset
                (id, meeting_id, source, path, sample_rate, channels, bytes, duration_ms, sha256, created_at)
            VALUES ($id, $meeting, $source, $path, $rate, $ch, $bytes, $dur, $sha, $created);
            """;
        cmd.Parameters.AddWithValue("$id", asset.Id);
        cmd.Parameters.AddWithValue("$meeting", asset.MeetingId);
        cmd.Parameters.AddWithValue("$source", asset.Source.ToString());
        cmd.Parameters.AddWithValue("$path", asset.Path);
        cmd.Parameters.AddWithValue("$rate", asset.SampleRate);
        cmd.Parameters.AddWithValue("$ch", asset.Channels);
        cmd.Parameters.AddWithValue("$bytes", asset.Bytes);
        cmd.Parameters.AddWithValue("$dur", asset.DurationMs);
        cmd.Parameters.AddWithValue("$sha", (object?)asset.Sha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", Iso(asset.CreatedAt));
        cmd.ExecuteNonQuery();
        return asset;
    }

    public AudioAssetRecord? GetAudio(string id)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mtg_audio_asset WHERE id = $id AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAudio(r) : null;
    }

    public IReadOnlyList<AudioAssetRecord> ListAudio(string meetingId)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mtg_audio_asset WHERE meeting_id = $m AND deleted_at IS NULL ORDER BY source;";
        cmd.Parameters.AddWithValue("$m", meetingId);
        using var r = cmd.ExecuteReader();
        var list = new List<AudioAssetRecord>();
        while (r.Read()) list.Add(ReadAudio(r));
        return list;
    }

    // ── 转写 run ────────────────────────────────────────────────────────

    public TranscriptRunRecord InsertRun(TranscriptRunRecord run)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mtg_transcript_run
                (id, meeting_id, engine, model, params_json, status, audio_asset_id, started_at, finished_at, error_code, error_message)
            VALUES ($id, $meeting, $engine, $model, $params, $status, $audio, $started, $finished, $code, $msg);
            """;
        cmd.Parameters.AddWithValue("$id", run.Id);
        cmd.Parameters.AddWithValue("$meeting", run.MeetingId);
        cmd.Parameters.AddWithValue("$engine", run.Engine);
        cmd.Parameters.AddWithValue("$model", run.Model);
        cmd.Parameters.AddWithValue("$params", run.ParamsJson);
        cmd.Parameters.AddWithValue("$status", run.Status.ToString());
        cmd.Parameters.AddWithValue("$audio", (object?)run.AudioAssetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$started", Iso(run.StartedAt));
        cmd.Parameters.AddWithValue("$finished", (object?)IsoOrNull(run.FinishedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$code", (object?)run.ErrorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$msg", (object?)run.ErrorMessage ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return run;
    }

    public TranscriptRunRecord? GetRun(string id)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mtg_transcript_run WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRun(r) : null;
    }

    public IReadOnlyList<TranscriptRunRecord> ListRuns(string meetingId)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mtg_transcript_run WHERE meeting_id = $m ORDER BY started_at DESC;";
        cmd.Parameters.AddWithValue("$m", meetingId);
        using var r = cmd.ExecuteReader();
        var list = new List<TranscriptRunRecord>();
        while (r.Read()) list.Add(ReadRun(r));
        return list;
    }

    public void UpdateRunStatus(string runId, TranscriptRunStatus status, string? errorCode = null, string? errorMessage = null, DateTimeOffset? finishedAt = null)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE mtg_transcript_run
               SET status = $status,
                   finished_at = COALESCE($finished, finished_at),
                   error_code = COALESCE($code, error_code),
                   error_message = COALESCE($msg, error_message)
             WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$status", status.ToString());
        cmd.Parameters.AddWithValue("$finished", (object?)IsoOrNull(finishedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$code", (object?)errorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$msg", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>进程重启后把"卡在 running"的 run 标成 interrupted，允许从第 N 段续转。</summary>
    public int MarkRunningAsInterrupted()
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE mtg_transcript_run SET status = $to, error_code = $code WHERE status IN ($queued, $running);";
        cmd.Parameters.AddWithValue("$to", TranscriptRunStatus.Interrupted.ToString());
        cmd.Parameters.AddWithValue("$code", MeetingErrorCodes.AsrFailed);
        cmd.Parameters.AddWithValue("$queued", TranscriptRunStatus.Queued.ToString());
        cmd.Parameters.AddWithValue("$running", TranscriptRunStatus.Running.ToString());
        return cmd.ExecuteNonQuery();
    }

    // ── 段 ──────────────────────────────────────────────────────────────

    public void InsertSegments(string runId, IEnumerable<TranscriptSegmentRecord> segments)
    {
        using var conn = _db.Connect();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO mtg_segment
                (id, run_id, ordinal, start_ms, end_ms, text, confidence, lang, speaker, speaker_source, engine, warnings)
            VALUES ($id, $run, $ord, $start, $end, $text, $conf, $lang, $speaker, $source, $engine, $warn);
            """;

        var id = cmd.Parameters.Add("$id", SqliteType.Text);
        var run = cmd.Parameters.Add("$run", SqliteType.Text);
        var ord = cmd.Parameters.Add("$ord", SqliteType.Integer);
        var start = cmd.Parameters.Add("$start", SqliteType.Integer);
        var end = cmd.Parameters.Add("$end", SqliteType.Integer);
        var text = cmd.Parameters.Add("$text", SqliteType.Text);
        var conf = cmd.Parameters.Add("$conf", SqliteType.Real);
        var lang = cmd.Parameters.Add("$lang", SqliteType.Text);
        var speaker = cmd.Parameters.Add("$speaker", SqliteType.Text);
        var source = cmd.Parameters.Add("$source", SqliteType.Text);
        var engine = cmd.Parameters.Add("$engine", SqliteType.Text);
        var warn = cmd.Parameters.Add("$warn", SqliteType.Text);

        foreach (var s in segments)
        {
            id.Value = s.Id;
            run.Value = s.RunId;
            ord.Value = s.Ordinal;
            start.Value = s.StartMs;
            end.Value = s.EndMs;
            text.Value = s.Text;
            conf.Value = (object?)s.Confidence ?? DBNull.Value;
            lang.Value = (object?)s.Language ?? DBNull.Value;
            speaker.Value = (object?)s.Speaker ?? DBNull.Value;
            source.Value = s.SpeakerSource.ToString();
            engine.Value = (object?)s.Engine ?? DBNull.Value;
            warn.Value = s.Warnings.Count == 0 ? DBNull.Value : string.Join(',', s.Warnings.Select(w => w.ToString()));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<TranscriptSegmentRecord> ListSegments(string runId)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM mtg_segment WHERE run_id = $run ORDER BY ordinal;";
        cmd.Parameters.AddWithValue("$run", runId);
        using var r = cmd.ExecuteReader();
        var list = new List<TranscriptSegmentRecord>();
        while (r.Read()) list.Add(ReadSegment(r));
        return list;
    }

    public TranscriptSlice? GetSlice(string runId)
    {
        var run = GetRun(runId);
        return run is null ? null : new TranscriptSlice(run, ListSegments(runId));
    }

    /// <summary>标注说话人（v1 手工）。speaker 为空表示清除标注。</summary>
    public int UpdateSegmentSpeaker(string runId, int ordinal, string? speaker, SpeakerSource source)
    {
        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE mtg_segment SET speaker = $speaker, speaker_source = $source
             WHERE run_id = $run AND ordinal = $ord;
            """;
        cmd.Parameters.AddWithValue("$speaker", string.IsNullOrWhiteSpace(speaker) ? DBNull.Value : speaker.Trim());
        cmd.Parameters.AddWithValue("$source", source.ToString());
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$ord", ordinal);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>一批 run 各自有多少段（列表页用，避免 N+1 次查询）。</summary>
    public IReadOnlyDictionary<string, int> CountSegmentsByRun(IReadOnlyCollection<string> runIds)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (runIds.Count == 0) return result;

        using var conn = _db.Connect();
        using var cmd = conn.CreateCommand();
        var names = new List<string>(runIds.Count);
        var i = 0;
        foreach (var id in runIds)
        {
            var name = "$r" + i++;
            names.Add(name);
            cmd.Parameters.AddWithValue(name, id);
        }

        cmd.CommandText = $"SELECT run_id, COUNT(*) FROM mtg_segment WHERE run_id IN ({string.Join(",", names)}) GROUP BY run_id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>按 ordinal 批量回写质检告警（不新增行，只改 warnings 列）。</summary>
    public void UpdateSegmentWarnings(string runId, IReadOnlyDictionary<int, IReadOnlyList<AsrWarningCode>> warnings)
    {
        if (warnings.Count == 0) return;

        using var conn = _db.Connect();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE mtg_segment SET warnings = $warn WHERE run_id = $run AND ordinal = $ord;";
        var warn = cmd.Parameters.Add("$warn", SqliteType.Text);
        cmd.Parameters.AddWithValue("$run", runId);
        var ord = cmd.Parameters.Add("$ord", SqliteType.Integer);

        foreach (var (ordinal, codes) in warnings)
        {
            if (codes.Count == 0) continue;
            warn.Value = string.Join(',', codes.Select(c => c.ToString()));
            ord.Value = ordinal;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ── 行映射 ──────────────────────────────────────────────────────────

    private static MeetingRecord ReadMeeting(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("title")),
        ParseEnum<MeetingKind>(r.GetString(r.GetOrdinal("kind"))),
        ParseIso(r.GetString(r.GetOrdinal("start_at"))),
        ReadIsoOrNull(r, "end_at"),
        ParseEnum<MeetingStatus>(r.GetString(r.GetOrdinal("status"))),
        ReadStringOrNull(r, "current_run_id"),
        ParseIso(r.GetString(r.GetOrdinal("created_at"))),
        ParseIso(r.GetString(r.GetOrdinal("updated_at"))));

    private static AudioAssetRecord ReadAudio(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("meeting_id")),
        ParseEnum<AudioSource>(r.GetString(r.GetOrdinal("source"))),
        r.GetString(r.GetOrdinal("path")),
        r.GetInt32(r.GetOrdinal("sample_rate")),
        r.GetInt32(r.GetOrdinal("channels")),
        r.GetInt64(r.GetOrdinal("bytes")),
        r.GetInt64(r.GetOrdinal("duration_ms")),
        ReadStringOrNull(r, "sha256"),
        ParseIso(r.GetString(r.GetOrdinal("created_at"))));

    private static TranscriptRunRecord ReadRun(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("meeting_id")),
        r.GetString(r.GetOrdinal("engine")),
        r.GetString(r.GetOrdinal("model")),
        r.GetString(r.GetOrdinal("params_json")),
        ParseEnum<TranscriptRunStatus>(r.GetString(r.GetOrdinal("status"))),
        ReadStringOrNull(r, "audio_asset_id"),
        ParseIso(r.GetString(r.GetOrdinal("started_at"))),
        ReadIsoOrNull(r, "finished_at"),
        ReadStringOrNull(r, "error_code"),
        ReadStringOrNull(r, "error_message"));

    private static TranscriptSegmentRecord ReadSegment(SqliteDataReader r)
    {
        var warnings = ReadStringOrNull(r, "warnings");
        return new TranscriptSegmentRecord(
            r.GetString(r.GetOrdinal("id")),
            r.GetString(r.GetOrdinal("run_id")),
            r.GetInt32(r.GetOrdinal("ordinal")),
            r.GetInt32(r.GetOrdinal("start_ms")),
            r.GetInt32(r.GetOrdinal("end_ms")),
            r.GetString(r.GetOrdinal("text")),
            r.IsDBNull(r.GetOrdinal("confidence")) ? null : r.GetDouble(r.GetOrdinal("confidence")),
            ReadStringOrNull(r, "lang"),
            ReadStringOrNull(r, "speaker"),
            ParseEnum<SpeakerSource>(r.GetString(r.GetOrdinal("speaker_source"))),
            ReadStringOrNull(r, "engine"),
            string.IsNullOrWhiteSpace(warnings)
                ? Array.Empty<AsrWarningCode>()
                : warnings.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => ParseEnum<AsrWarningCode>(w)).ToArray());
    }

    // ── 小工具 ──────────────────────────────────────────────────────────

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static string? IsoOrNull(DateTimeOffset? value) => value is null ? null : Iso(value.Value);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ReadIsoOrNull(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : ParseIso(r.GetString(i));
    }

    private static string? ReadStringOrNull(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.Parse<T>(value, ignoreCase: true);
}
