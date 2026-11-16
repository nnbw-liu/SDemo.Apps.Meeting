using System.Text.Json.Nodes;

namespace SDemo.App.Meeting.Contracts;

/// <summary>会议向 sdemo.kb 的投影状态。写入是自动的（像日志），定格是显式的（像档案）。</summary>
public enum ProjectionStatus
{
    None,
    Pending,
    Done,
    Failed,
}

/// <summary>一场会议在 KB 里的投影记录。docId 由 KB 生成，重复投影要幂等。</summary>
public sealed record ProjectionRecord(
    string MeetingId,
    ProjectionStatus Status,
    string? DocId,
    int? DocVersion,
    int BlockCount,
    bool IncludeTranscript,
    int Attempts,
    string? LastError,
    string IdempotencyKey,
    DateTimeOffset UpdatedAt)
{
    public static ProjectionRecord None(string meetingId, string idempotencyKey) => new(
        meetingId, ProjectionStatus.None, null, null, 0, false, 0, null, idempotencyKey, DateTimeOffset.UtcNow);
}

/// <summary>Kb 写入面（kb-design §3.4）的请求体。</summary>
public sealed record KbIngestDocument(
    string Title,
    string Kind,
    string Retention,
    string IdempotencyKey,
    JsonArray Blocks,
    string EntityUri,
    string? Rationale = null)
{
    public JsonObject ToJson() => new()
    {
        ["source"] = new JsonObject
        {
            ["appId"] = MeetingConstants.AppId,
            ["entityUri"] = EntityUri,
            ["projectionVersion"] = 1,
        },
        ["kind"] = Kind,
        ["title"] = Title,
        ["retention"] = Retention,
        ["rationale"] = Rationale,
        ["idempotencyKey"] = IdempotencyKey,
        ["blocks"] = Blocks.DeepClone(),
    };
}

/// <summary>KB 写入面的返回（docId + version 用于后续 promote / revise）。</summary>
public sealed record KbIngestResult(string? DocId, int? Version, string? Error);
