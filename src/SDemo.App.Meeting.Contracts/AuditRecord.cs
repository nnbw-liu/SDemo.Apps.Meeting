namespace SDemo.App.Meeting.Contracts;

/// <summary>谁发起的写操作。MCP 写入固定为 agent，界面/CLI 为 user。</summary>
public enum AuditActor
{
    User,
    Agent,
    System,
}

/// <summary>
/// 一条写操作审计。**只存参数摘要与结果，不存正文全文**（铁律 5：MCP 写操作要落审计）。
/// </summary>
public sealed record AuditRecord(
    string Id,
    DateTimeOffset At,
    AuditActor Actor,
    string Tool,
    string Action,
    string EntityType,
    string? EntityId,
    string? MeetingId,
    string? ArgsSummary,
    string Outcome,
    string? Error);
