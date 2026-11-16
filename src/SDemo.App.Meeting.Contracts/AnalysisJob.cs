namespace SDemo.App.Meeting.Contracts;

/// <summary>
/// 会议分析的作业状态。Meeting **不做模型调用**（铁律：零凭证），只维护队列；
/// 真正的 map-reduce 由 DesktopAgent 驱动：claim → 逐段抽 → 用写工具落库 → complete。
/// </summary>
public enum AnalysisJobStatus
{
    Queued,
    Running,
    Done,
    Failed,
}

/// <summary>一次分析作业。同一场会议同时只允许一个 Queued/Running。</summary>
public sealed record AnalysisJob(
    string Id,
    string MeetingId,
    AnalysisJobStatus Status,
    string RequestedBy,
    string? PromptVersion,
    string? Provider,
    int SegmentsTotal,
    int SegmentsDone,
    int TopicsCreated,
    int DecisionsCreated,
    int ActionsCreated,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);
