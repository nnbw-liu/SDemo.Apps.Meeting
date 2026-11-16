using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;

namespace SDemo.Meeting.Tests;

/// <summary>
/// 分析队列：Meeting 只排队，Agent 来 claim / finish。
/// 关键保证是"同一场会议只排一次"和"两个 Agent 不会抢同一个作业"。
/// </summary>
public sealed class AnalysisQueueTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly MeetingRecord _meeting;
    private readonly AnalysisQueueService _queue;

    public AnalysisQueueTests()
    {
        var now = DateTimeOffset.UtcNow;
        _meeting = _db.Session.InsertMeeting(new MeetingRecord(
            MeetingIds.NewId(), "待分析", MeetingKind.Weekly, now, null,
            MeetingStatus.Ready, null, now, now));

        var run = new TranscriptRunRecord(
            MeetingIds.NewId(), _meeting.Id, "whisper-cpp", "turbo", "{}",
            TranscriptRunStatus.Done, null, now, now, null, null);
        _db.Session.InsertRun(run);
        _db.Session.InsertSegments(run.Id, new[]
        {
            Segment(run.Id, 0, 0, 1000, "第一段"),
            Segment(run.Id, 1, 1000, 2000, "第二段"),
            Segment(run.Id, 2, 2000, 3000, "第三段"),
        });
        _db.Session.SetCurrentRun(_meeting.Id, run.Id);

        _queue = new AnalysisQueueService(_db.Session);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void 入队会带上段数并处于排队态()
    {
        var job = _queue.Request(_meeting.Id);

        Assert.Equal(AnalysisJobStatus.Queued, job.Status);
        Assert.Equal(3, job.SegmentsTotal);
        Assert.Equal(AnalysisQueueService.RequestedByUser, job.RequestedBy);
    }

    [Fact]
    public void 排队期间重复请求是幂等的()
    {
        var first = _queue.Request(_meeting.Id);
        var second = _queue.Request(_meeting.Id);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void 跑完之后可以再排一次()
    {
        var first = _queue.Request(_meeting.Id);
        var claimed = _queue.Claim()!;
        _queue.Complete(claimed.Id, 1, 1, 0);

        var second = _queue.Request(_meeting.Id);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(AnalysisJobStatus.Queued, second.Status);
    }

    [Fact]
    public void claim_会把作业置为运行中且只能被取走一次()
    {
        _queue.Request(_meeting.Id);

        var first = _queue.Claim();
        var second = _queue.Claim();

        Assert.NotNull(first);
        Assert.Equal(AnalysisJobStatus.Running, first!.Status);
        Assert.NotNull(first.StartedAt);
        Assert.Null(second);
    }

    [Fact]
    public void 完成会记下产出条数()
    {
        _queue.Request(_meeting.Id);
        var claimed = _queue.Claim()!;

        var done = _queue.Complete(claimed.Id, topicsCreated: 3, decisionsCreated: 2, actionsCreated: 4, provider: "deepseek-flash");

        Assert.Equal(AnalysisJobStatus.Done, done.Status);
        Assert.Equal(3, done.TopicsCreated);
        Assert.Equal(2, done.DecisionsCreated);
        Assert.Equal(4, done.ActionsCreated);
        Assert.Equal("deepseek-flash", done.Provider);
        Assert.Equal(done.SegmentsTotal, done.SegmentsDone);
        Assert.NotNull(done.FinishedAt);
    }

    [Fact]
    public void 失败会记下错误并可重新排队()
    {
        _queue.Request(_meeting.Id);
        var claimed = _queue.Claim()!;

        var failed = _queue.Fail(claimed.Id, "模型返回不是 JSON");

        Assert.Equal(AnalysisJobStatus.Failed, failed.Status);
        Assert.Contains("JSON", failed.Error);

        var retry = _queue.Request(_meeting.Id);
        Assert.Equal(AnalysisJobStatus.Queued, retry.Status);
    }

    [Fact]
    public void 进度可以分段上报()
    {
        _queue.Request(_meeting.Id);
        var claimed = _queue.Claim()!;

        var progress = _queue.Progress(claimed.Id, 2);

        Assert.Equal(2, progress.SegmentsDone);
    }

    [Fact]
    public void 没有转写就不能排分析()
    {
        var now = DateTimeOffset.UtcNow;
        var empty = _db.Session.InsertMeeting(new MeetingRecord(
            MeetingIds.NewId(), "没转写", MeetingKind.AdHoc, now, null,
            MeetingStatus.Ready, null, now, now));

        var ex = Assert.Throws<MeetingException>(() => _queue.Request(empty.Id));
        Assert.Contains("转写", ex.Message);
    }

    [Fact]
    public void 会议不存在时直接报错()
    {
        var ex = Assert.Throws<MeetingException>(() => _queue.Request("nope"));
        Assert.Equal(MeetingErrorCodes.MeetingNotFound, ex.Code);
    }

    private static TranscriptSegmentRecord Segment(string runId, int ordinal, int start, int end, string text) =>
        new(MeetingIds.NewId(), runId, ordinal, start, end, text, 0.9, "zh",
            null, SpeakerSource.None, null, Array.Empty<AsrWarningCode>());
}
