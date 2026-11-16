using System.Text.Json.Nodes;
using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;

namespace SDemo.Meeting.Tests;

/// <summary>KB 投影：写入自动、定格显式、KB 不在也不阻塞（kb-design §3/§4）。</summary>
public sealed class ProjectionTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly MeetingRecord _meeting;
    private readonly TranscriptRunRecord _run;

    public ProjectionTests()
    {
        var now = DateTimeOffset.UtcNow;
        _meeting = _db.Session.InsertMeeting(new MeetingRecord(
            MeetingIds.NewId(), "部署方案评审", MeetingKind.Weekly, now.AddMinutes(-30), now,
            MeetingStatus.Ready, null, now, now));

        _run = new TranscriptRunRecord(
            MeetingIds.NewId(), _meeting.Id, "whisper-cpp", "turbo", "{}",
            TranscriptRunStatus.Done, null, now, now, null, null);
        _db.Session.InsertRun(_run);
        _db.Session.InsertSegments(_run.Id, new[]
        {
            Segment(_run.Id, 0, 0, 1200, "今天过一下部署方案"),
            Segment(_run.Id, 1, 1200, 2600, "回滚时间没人算过"),
        });
        _db.Session.SetCurrentRun(_meeting.Id, _run.Id);

        var domain = new MeetingDomainService(_db.Session);
        var topic = domain.AddTopic(_meeting.Id, "部署方案", refs: new[] { "sdemo.kb/doc/01H9@v7" });
        var decision = domain.AddDecision(_meeting.Id, "先灰度两周再切全量", topic.Id, owner: "张三", anchorMs: 1200);
        domain.ConfirmDecision(decision.Id);
        domain.AddAction(_meeting.Id, "补回滚演练记录", topic.Id, owner: "王五", anchorMs: 1200);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task 投影成功会记下_docId_与块数()
    {
        var transport = new FakeTransport();
        var service = CreateService(transport);

        var record = await service.ProjectAsync(_meeting.Id);

        Assert.Equal(ProjectionStatus.Done, record.Status);
        Assert.Equal("doc-001", record.DocId);
        Assert.Equal(7, record.DocVersion);
        Assert.True(record.BlockCount > 0);
        Assert.Equal(1, record.Attempts);
        Assert.Null(record.LastError);
    }

    [Fact]
    public async Task KB_不在时记失败_之后可重试成功()
    {
        IKbTransport? current = null;
        var service = new KbProjectionService(
            _db.Session,
            new MeetingDomainService(_db.Session),
            () => current);

        var failed = await service.ProjectAsync(_meeting.Id);

        Assert.Equal(ProjectionStatus.Failed, failed.Status);
        Assert.Equal(1, failed.Attempts);
        Assert.Contains("KB", failed.LastError);

        // KB 起来了，再投一次
        current = new FakeTransport();
        var done = await service.ProjectAsync(_meeting.Id);

        Assert.Equal(ProjectionStatus.Done, done.Status);
        Assert.Equal(2, done.Attempts);
        Assert.Null(done.LastError);
    }

    [Fact]
    public void 相同内容幂等键稳定_带不带转写不同()
    {
        var domain = new MeetingDomainService(_db.Session).Get(_meeting.Id);
        var slice = _db.Session.GetSlice(_run.Id);

        var a = KbProjectionService.BuildDocument(domain, slice, includeTranscript: false);
        var b = KbProjectionService.BuildDocument(domain, slice, includeTranscript: false);
        var withTranscript = KbProjectionService.BuildDocument(domain, slice, includeTranscript: true);

        Assert.Equal(a.IdempotencyKey, b.IdempotencyKey);
        Assert.NotEqual(a.IdempotencyKey, withTranscript.IdempotencyKey);
    }

    [Fact]
    public void 文档里同时有议题决议与候选()
    {
        var domain = new MeetingDomainService(_db.Session).Get(_meeting.Id);
        var document = KbProjectionService.BuildDocument(domain, null, includeTranscript: false);
        var text = string.Join("\n", document.Blocks.Select(b => b!["text"]!.GetValue<string>()));

        Assert.Contains("议题 1：部署方案", text);
        Assert.Contains("决议（已确认·00:00:01）：先灰度两周再切全量", text);
        Assert.Contains("待办候选（待安排·王五·00:00:01）：补回滚演练记录", text);
        Assert.Contains("sdemo.kb/doc/01H9@v7", text);
        Assert.Equal("decision", document.Kind);
        Assert.Equal("durable", document.Retention);
        Assert.StartsWith("sdemo.meeting/meeting/", document.EntityUri);
    }

    [Fact]
    public void 默认不投影转写_显式归档才写()
    {
        var domain = new MeetingDomainService(_db.Session).Get(_meeting.Id);
        var slice = _db.Session.GetSlice(_run.Id);

        var plain = KbProjectionService.BuildDocument(domain, slice, includeTranscript: false);
        var withTranscript = KbProjectionService.BuildDocument(domain, slice, includeTranscript: true);

        Assert.DoesNotContain(plain.Blocks,
            b => b!["text"]!.GetValue<string>().Contains("[00:00:00] 今天过一下部署方案"));

        Assert.Contains(withTranscript.Blocks,
            b => b!["text"]!.GetValue<string>() == "[00:00:00] 今天过一下部署方案");
    }

    [Fact]
    public async Task 归档直接以_durable_写入_不走_promote()
    {
        var transport = new FakeTransport();
        var service = CreateService(transport);

        await service.ProjectAsync(_meeting.Id, includeTranscript: true);

        // KB 只让 KB 界面 / Agent 调 promote（应用自己调 403），所以归档必须一次写成 durable
        Assert.Contains("\"durable\"", transport.LastIngestJson);
        Assert.Null(transport.LastPromoteJson);
    }

    [Fact]
    public void 可以显式指定_staging_与_durable_两种保留级别()
    {
        var domain = new MeetingDomainService(_db.Session).Get(_meeting.Id);

        var staging = KbProjectionService.BuildDocument(domain, null, false, KbProjectionService.StagingRetention);
        var durable = KbProjectionService.BuildDocument(domain, null, false, KbProjectionService.DurableRetention);

        Assert.Equal("staging", staging.Retention);
        Assert.Equal("durable", durable.Retention);
        Assert.NotEqual(staging.IdempotencyKey, durable.IdempotencyKey);
    }

    [Fact]
    public async Task 没投影过就升格会报错()
    {
        var service = CreateService(new FakeTransport());

        var ex = await Assert.ThrowsAsync<MeetingException>(() => service.PromoteAsync(_meeting.Id));
        Assert.Equal(MeetingErrorCodes.KbUnavailable, ex.Code);
    }

    [Fact]
    public async Task KB_返回错误时保留错误信息()
    {
        var service = CreateService(new FakeTransport { IngestError = "HTTP 401：token 无效" });

        var record = await service.ProjectAsync(_meeting.Id);

        Assert.Equal(ProjectionStatus.Failed, record.Status);
        Assert.Contains("401", record.LastError);
    }

    private KbProjectionService CreateService(IKbTransport transport) => new(
        _db.Session,
        new MeetingDomainService(_db.Session),
        () => transport);

    private static TranscriptSegmentRecord Segment(string runId, int ordinal, int start, int end, string text) =>
        new(MeetingIds.NewId(), runId, ordinal, start, end, text, 0.9, "zh",
            null, SpeakerSource.None, null, Array.Empty<AsrWarningCode>());

    private sealed class FakeTransport : IKbTransport
    {
        public string? IngestError { get; set; }

        public string? LastIngestJson { get; private set; }

        public string? LastPromoteJson { get; private set; }

        public Task<KbIngestResult> IngestAsync(string json, CancellationToken ct)
        {
            LastIngestJson = json;
            return Task.FromResult(IngestError is null
                ? new KbIngestResult("doc-001", 7, null)
                : new KbIngestResult(null, null, IngestError));
        }

        public Task<KbIngestResult> PromoteAsync(string json, CancellationToken ct)
        {
            LastPromoteJson = json;
            return Task.FromResult(new KbIngestResult("doc-001", 8, null));
        }
    }
}
