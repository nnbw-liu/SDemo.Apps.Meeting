using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

/// <summary>
/// 分析队列。**Meeting 不调模型**——它只负责：
///   1. 用户点「分析」→ 入队（同一场会议同时只允许一个 Queued/Running，重复点幂等）；
///   2. Agent 来 claim（原子置 Running，两个 Agent 不会抢同一场）；
///   3. Agent 逐段抽完、用写工具落库后，complete 记下产出条数；失败记 error 可重试。
///
/// 真正的 map-reduce 在 DesktopAgent：claim → 分页取 `meeting_transcript` → 抽 → 写。
/// </summary>
public sealed class AnalysisQueueService
{
    public const string RequestedByUser = "user";
    public const string RequestedByAgent = "agent";

    private readonly Storage.MeetingSession _session;
    private readonly TimeProvider _clock;

    public AnalysisQueueService(Storage.MeetingSession session, TimeProvider? clock = null)
    {
        _session = session;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>入队。已有 Queued/Running 时直接返回它（幂等，不重复排）。</summary>
    public AnalysisJob Request(string meetingId, string requestedBy = RequestedByUser, string? promptVersion = null)
    {
        var meeting = _session.GetMeeting(meetingId)
                      ?? throw new MeetingException(MeetingErrorCodes.MeetingNotFound, $"会议不存在：{meetingId}");

        var latest = _session.GetLatestAnalysis(meetingId);
        if (latest is { Status: AnalysisJobStatus.Queued or AnalysisJobStatus.Running })
            return latest;

        var segments = meeting.CurrentRunId is { } runId ? _session.ListSegments(runId).Count : 0;
        if (segments == 0)
            throw new MeetingException(MeetingErrorCodes.InvalidArgument, "这场会议还没有转写结果，先转写再分析。");

        return _session.InsertAnalysis(new AnalysisJob(
            MeetingIds.NewId(),
            meetingId,
            AnalysisJobStatus.Queued,
            requestedBy,
            promptVersion,
            Provider: null,
            SegmentsTotal: segments,
            SegmentsDone: 0,
            TopicsCreated: 0,
            DecisionsCreated: 0,
            ActionsCreated: 0,
            Error: null,
            CreatedAt: _clock.GetUtcNow(),
            StartedAt: null,
            FinishedAt: null));
    }

    /// <summary>Agent 来取件。没有排队作业返回 null。</summary>
    public AnalysisJob? Claim() => _session.ClaimNextAnalysis(_clock.GetUtcNow());

    public AnalysisJob Latest(string meetingId) =>
        _session.GetLatestAnalysis(meetingId)
        ?? new AnalysisJob(string.Empty, meetingId, AnalysisJobStatus.Queued, string.Empty, null, null, 0, 0, 0, 0, 0,
            null, _clock.GetUtcNow(), null, null);

    public AnalysisJob Progress(string jobId, int segmentsDone)
    {
        _session.UpdateAnalysisProgress(jobId, segmentsDone);
        return _session.GetAnalysis(jobId)
               ?? throw new MeetingException(MeetingErrorCodes.InvalidArgument, $"分析作业不存在：{jobId}");
    }

    public AnalysisJob Complete(string jobId, int topicsCreated, int decisionsCreated, int actionsCreated, string? provider = null)
    {
        var job = _session.GetAnalysis(jobId)
                  ?? throw new MeetingException(MeetingErrorCodes.InvalidArgument, $"分析作业不存在：{jobId}");

        if (job.Status == AnalysisJobStatus.Done) return job;

        return _session.CompleteAnalysis(jobId, topicsCreated, decisionsCreated, actionsCreated, provider, _clock.GetUtcNow())
               ?? throw new MeetingException(MeetingErrorCodes.InvalidArgument, $"分析作业不存在：{jobId}");
    }

    public AnalysisJob Fail(string jobId, string error)
    {
        _ = _session.GetAnalysis(jobId)
            ?? throw new MeetingException(MeetingErrorCodes.InvalidArgument, $"分析作业不存在：{jobId}");

        return _session.FailAnalysis(jobId, error, _clock.GetUtcNow())!;
    }
}
