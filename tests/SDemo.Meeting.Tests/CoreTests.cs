using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;

namespace SDemo.Meeting.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task 正常转写_落段并切换_current_run()
    {
        var meeting = _db.Session.InsertMeeting(NewMeeting("周会"));
        var audio = AttachAudio(meeting.Id);
        var provider = new FakeProvider(("第一段", 0, 1200), ("第二段", 1200, 3000));

        var runner = new TranscriptionRunner(new MeetingService(_db.Session));
        var slice = await runner.RunAsync(meeting.Id, audio.Path, provider, language: "zh");

        Assert.Equal(2, slice.Segments.Count);
        Assert.Equal("第一段", slice.Segments[0].Text);
        Assert.Equal(TranscriptRunStatus.Done, slice.Run.Status);
        Assert.Equal(1200, slice.Segments[0].EndMs);

        var reloaded = _db.Session.GetMeeting(meeting.Id)!;
        Assert.Equal(slice.Run.Id, reloaded.CurrentRunId);
        Assert.Equal(MeetingStatus.Ready, reloaded.Status);
    }

    [Fact]
    public async Task 引擎不可用_不建_run_直接报错()
    {
        var meeting = _db.Session.InsertMeeting(NewMeeting("周会"));
        var audio = AttachAudio(meeting.Id);
        var provider = new FakeProvider { Available = false };

        var runner = new TranscriptionRunner(new MeetingService(_db.Session));

        var ex = await Assert.ThrowsAsync<MeetingException>(() =>
            runner.RunAsync(meeting.Id, audio.Path, provider));

        Assert.Equal(MeetingErrorCodes.AsrUnavailable, ex.Code);
        Assert.Empty(_db.Session.ListRuns(meeting.Id));
    }

    [Fact]
    public async Task 取消_把_run_标成_cancelled_且不切_current()
    {
        var meeting = _db.Session.InsertMeeting(NewMeeting("周会"));
        var audio = AttachAudio(meeting.Id);
        var provider = new FakeProvider { HangUntilCancelled = true };

        var runner = new TranscriptionRunner(new MeetingService(_db.Session));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(meeting.Id, audio.Path, provider, ct: cts.Token));

        var run = Assert.Single(_db.Session.ListRuns(meeting.Id));
        Assert.Equal(TranscriptRunStatus.Cancelled, run.Status);

        var reloaded = _db.Session.GetMeeting(meeting.Id)!;
        Assert.Null(reloaded.CurrentRunId);
        Assert.Equal(MeetingStatus.Ready, reloaded.Status);
    }

    [Fact]
    public async Task 引擎抛异常_把_run_标成_failed()
    {
        var meeting = _db.Session.InsertMeeting(NewMeeting("周会"));
        var audio = AttachAudio(meeting.Id);
        var provider = new FakeProvider { Throws = new InvalidOperationException("boom") };

        var runner = new TranscriptionRunner(new MeetingService(_db.Session));

        var ex = await Assert.ThrowsAsync<MeetingException>(() =>
            runner.RunAsync(meeting.Id, audio.Path, provider));

        Assert.Equal(MeetingErrorCodes.AsrFailed, ex.Code);
        var run = Assert.Single(_db.Session.ListRuns(meeting.Id));
        Assert.Equal(TranscriptRunStatus.Failed, run.Status);
        Assert.Contains("boom", run.ErrorMessage);
    }

    [Fact]
    public void 质检_语言切换密集会给出警告()
    {
        var segments = Enumerable.Range(0, 8)
            .Select(i => new AsrSegment(i * 1000, (i + 1) * 1000, "x", 0.9f, i % 2 == 0 ? "zh" : "en"))
            .ToArray();

        var warnings = TranscriptQuality.Analyze(segments);

        Assert.Contains(warnings, w => w.Code == AsrWarningCode.LangSwitch);
    }

    [Fact]
    public void 质检_空结果给出_empty_output()
    {
        var warnings = TranscriptQuality.Analyze(Array.Empty<AsrSegment>());
        Assert.Single(warnings);
        Assert.Equal(AsrWarningCode.EmptyOutput, warnings[0].Code);
    }

    [Fact]
    public void 质检_声明中文却输出假名判为字符集不符()
    {
        var segments = new[] { new AsrSegment(0, 1000, "では、次にリリースの話をします", 0.9f) };
        var warnings = TranscriptQuality.Analyze(segments, declaredLanguage: "zh");
        Assert.Contains(warnings, w => w.Code == AsrWarningCode.CharsetMismatch);
    }

    private MeetingRecord NewMeeting(string title)
    {
        var now = DateTimeOffset.UtcNow;
        return new MeetingRecord(MeetingIds.NewId(), title, MeetingKind.Weekly, now, null,
            MeetingStatus.Ready, null, now, now);
    }

    private AudioAssetRecord AttachAudio(string meetingId)
    {
        var dir = Path.Combine(_db.Dir, "audio");
        var path = TestAudio.WriteSilence(Path.Combine(dir, "a.wav"), seconds: 2);
        var service = new MeetingService(_db.Session);
        return service.AttachAudio(meetingId, AudioSource.Microphone, path);
    }
}

/// <summary>假引擎：只测编排，不碰真实 whisper。</summary>
internal sealed class FakeProvider : IAsrProvider
{
    private readonly (string Text, int Start, int End)[] _segments;

    public FakeProvider(params (string Text, int Start, int End)[] segments) => _segments = segments;

    public bool Available { get; init; } = true;

    public bool HangUntilCancelled { get; init; }

    public Exception? Throws { get; init; }

    public string Id => "fake";

    public AsrCapabilities Capabilities { get; } = new(
        TimestampPrecision.Segment, PunctuationMode.Native, new[] { "zh", "en", "ja" },
        false, false, false, AsrEndpointKind.InProcess);

    public IReadOnlyList<AsrModelPack> Packs { get; } =
        new[] { new AsrModelPack("fake-model", "Fake", 0, TimestampPrecision.Segment, new[] { "zh" }, false) };

    public AsrAvailability Probe() =>
        Available ? AsrAvailability.Ok() : AsrAvailability.No("故意不可用");

    public void Dispose()
    {
    }

    public async Task<AsrResult> TranscribeAsync(AsrJob job, IProgress<AsrProgress>? progress, CancellationToken ct)
    {
        if (Throws is not null) throw Throws;

        if (HangUntilCancelled)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        ct.ThrowIfCancellationRequested();

        var segments = _segments.Length == 0
            ? new[] { new AsrSegment(0, 1000, "hello", 0.9f, "zh") }
            : _segments.Select(s => new AsrSegment(s.Start, s.End, s.Text, 0.9f, "zh")).ToArray();

        return new AsrResult(Id, "fake-model", TimestampPrecision.Segment, segments,
            Array.Empty<AsrWarning>(), TimeSpan.FromMilliseconds(1));
    }
}
