namespace SDemo.App.Meeting.Contracts;

/// <summary>议题：会议内部的一节，横切项目——refs 只存 URI 字符串，不解析对方结构。</summary>
public sealed record TopicRecord(
    string Id,
    string MeetingId,
    int Ordinal,
    string Title,
    string? Summary,
    IReadOnlyList<string> Refs,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DecisionRecord(
    string Id,
    string MeetingId,
    string? TopicId,
    string Text,
    string? Rationale,
    string? Owner,
    DecisionStatus Status,
    int? AnchorMs,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ActionRecord(
    string Id,
    string MeetingId,
    string? TopicId,
    string Text,
    string? Owner,
    DateTimeOffset? DueAt,
    ActionStatus Status,
    string? PlannerRef,
    int? AnchorMs,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsOpen => Status == ActionStatus.Candidate;
}

/// <summary>一场会议的完整领域结构：议题 → 决议 / 候选。</summary>
public sealed record MeetingDomain(
    MeetingRecord Meeting,
    IReadOnlyList<TopicRecord> Topics,
    IReadOnlyList<DecisionRecord> Decisions,
    IReadOnlyList<ActionRecord> Actions)
{
    public int ConfirmedDecisions => Decisions.Count(d => d.Status == DecisionStatus.Confirmed);
    public int CandidateActions => Actions.Count(a => a.Status == ActionStatus.Candidate);
}
