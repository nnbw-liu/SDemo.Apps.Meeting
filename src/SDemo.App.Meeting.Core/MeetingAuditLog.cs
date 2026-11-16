using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

/// <summary>
/// 写操作审计。规则：
/// 1. 参数只留**截断摘要**（默认 300 字符），正文不进审计表；
/// 2. 成功与失败都记（失败带错误码），否则"谁试过但被拒"就查不到；
/// 3. 审计不做软删——它是事实记录。
/// </summary>
public sealed class MeetingAuditLog
{
    public const int MaxSummaryChars = 300;

    private readonly Storage.MeetingSession _session;
    private readonly TimeProvider _clock;

    public MeetingAuditLog(Storage.MeetingSession session, TimeProvider? clock = null)
    {
        _session = session;
        _clock = clock ?? TimeProvider.System;
    }

    public AuditRecord Write(
        string tool,
        string action,
        string entityType,
        string? entityId = null,
        string? meetingId = null,
        string? argsJson = null,
        AuditActor actor = AuditActor.Agent)
    {
        var record = new AuditRecord(
            MeetingIds.NewId(),
            _clock.GetUtcNow(),
            actor,
            tool,
            action,
            entityType,
            entityId,
            meetingId,
            Summarize(argsJson),
            "ok",
            null);

        return _session.InsertAudit(record);
    }

    public AuditRecord WriteFailure(
        string tool,
        string action,
        string entityType,
        string error,
        string? entityId = null,
        string? meetingId = null,
        string? argsJson = null,
        AuditActor actor = AuditActor.Agent)
    {
        var record = new AuditRecord(
            MeetingIds.NewId(),
            _clock.GetUtcNow(),
            actor,
            tool,
            action,
            entityType,
            entityId,
            meetingId,
            Summarize(argsJson),
            "error",
            error.Length <= 500 ? error : error[..500]);

        return _session.InsertAudit(record);
    }

    public IReadOnlyList<AuditRecord> List(string? meetingId = null, int limit = 100) =>
        _session.ListAudit(meetingId, limit);

    public static string? Summarize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : json.Length <= MaxSummaryChars ? json : json[..MaxSummaryChars] + "…";
}
