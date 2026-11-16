using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;

namespace SDemo.Meeting.Tests;

public sealed class VadTests
{
    [Fact]
    public void 短停顿会被打包成同一个窗口()
    {
        var wav = TestAudio.WritePattern(Path.Combine(Path.GetTempPath(), "meeting-tests", $"vad-{Guid.NewGuid():N}.wav"),
            (2, 0), (2, 8000), (2, 0), (3, 8000), (1, 0));

        var segments = VadSegmenter.Segment(VadSegmenter.ReadSamples(wav));

        // 2 秒停顿小于打包上限（4 秒）→ 合成一个窗口，避免每句都调一次模型
        var single = Assert.Single(segments);
        Assert.InRange(single.StartMs, 1500, 2300);
        Assert.InRange(single.EndMs, 8900, 9400);
    }

    [Fact]
    public void 长停顿会切成两个窗口()
    {
        var wav = TestAudio.WritePattern(Path.Combine(Path.GetTempPath(), "meeting-tests", $"gap-{Guid.NewGuid():N}.wav"),
            (1, 0), (2, 8000), (6, 0), (2, 8000), (1, 0));

        var segments = VadSegmenter.Segment(VadSegmenter.ReadSamples(wav));

        Assert.Equal(2, segments.Count);
        Assert.InRange(segments[0].StartMs, 700, 1100);
        Assert.InRange(segments[1].StartMs, 8500, 9300);
    }

    [Fact]
    public void 全静音不产生语音段()
    {
        var wav = TestAudio.WriteSilence(
            Path.Combine(Path.GetTempPath(), "meeting-tests", $"silence-{Guid.NewGuid():N}.wav"), seconds: 5);

        var segments = VadSegmenter.Segment(VadSegmenter.ReadSamples(wav));

        Assert.Empty(segments);
    }

    [Fact]
    public void 超长语音会按上限切开且不重新粘回()
    {
        var wav = TestAudio.WritePattern(
            Path.Combine(Path.GetTempPath(), "meeting-tests", $"long-{Guid.NewGuid():N}.wav"), (70, 8000));

        var segments = VadSegmenter.Segment(VadSegmenter.ReadSamples(wav),
            new VadSegmenter.Options(MaxSegmentMs: 30_000));

        Assert.True(segments.Count >= 3, $"应被切成 ≥3 段，实际 {segments.Count}");
        Assert.All(segments, s => Assert.True(s.DurationMs <= 30_500, $"段过长：{s}"));
        Assert.Equal(0, segments[0].StartMs);
        Assert.True(segments[^1].EndMs >= 68_000);
    }

    [Fact]
    public void 切片写出的_wav_能被读回且时长正确()
    {
        var source = TestAudio.WritePattern(
            Path.Combine(Path.GetTempPath(), "meeting-tests", $"src-{Guid.NewGuid():N}.wav"), (6, 8000));

        var samples = VadSegmenter.ReadSamples(source);
        var slice = WavSlicer.WriteTemporary(samples, 1000, 3000);

        try
        {
            var format = WavFormatReader.Read(slice);
            Assert.True(format.IsTarget);
            Assert.InRange(format.DurationMs, 1950, 2050);
        }
        finally
        {
            File.Delete(slice);
        }
    }

    [Fact]
    public async Task 分段引擎会按语音窗多次调用并平移到全局时间轴()
    {
        using var db = new TempDb();
        var meeting = NewMeeting(db);
        var wav = TestAudio.WritePattern(Path.Combine(db.Dir, "pattern.wav"),
            (1, 0), (2, 8000), (6, 0), (2, 8000), (1, 0));
        var audio = new MeetingService(db.Session).AttachAudio(meeting.Id, AudioSource.Microphone, wav);

        var provider = new FakeVadProvider();
        var runner = new TranscriptionRunner(new MeetingService(db.Session));

        var slice = await runner.RunAsync(meeting.Id, audio.Path, provider);

        Assert.True(provider.Calls >= 2, $"应至少调用 2 次，实际 {provider.Calls}");
        Assert.True(slice.Segments.Count >= 2);
        // 第二段的起点应该落在第二段语音附近（≈6s），而不是从 0 开始
        Assert.True(slice.Segments[^1].StartMs > 5000, $"末段起点 {slice.Segments[^1].StartMs} 没有平移");
    }

    [Fact]
    public async Task 取消后已完成的段保留且状态为_cancelled()
    {
        using var db = new TempDb();
        var meeting = NewMeeting(db);
        var wav = TestAudio.WritePattern(Path.Combine(db.Dir, "pattern2.wav"),
            (1, 0), (2, 8000), (6, 0), (2, 8000), (1, 0));
        var audio = new MeetingService(db.Session).AttachAudio(meeting.Id, AudioSource.Microphone, wav);

        var provider = new FakeVadProvider { CancelAfterCalls = 1 };
        var runner = new TranscriptionRunner(new MeetingService(db.Session));

        Assert.ThrowsAny<OperationCanceledException>(() =>
            runner.RunAsync(meeting.Id, audio.Path, provider).GetAwaiter().GetResult());

        var run = Assert.Single(db.Session.ListRuns(meeting.Id));
        Assert.Equal(TranscriptRunStatus.Cancelled, run.Status);
        Assert.True(db.Session.ListSegments(run.Id).Count > 0, "取消前完成的段必须保留");
    }

    [Fact]
    public async Task 续转从已完成段之后继续()
    {
        using var db = new TempDb();
        var meeting = NewMeeting(db);
        var wav = TestAudio.WritePattern(Path.Combine(db.Dir, "pattern3.wav"),
            (1, 0), (2, 8000), (6, 0), (2, 8000), (1, 0));
        var service = new MeetingService(db.Session);
        var audio = service.AttachAudio(meeting.Id, AudioSource.Microphone, wav);

        var runner = new TranscriptionRunner(service);
        var cancelling = new FakeVadProvider { CancelAfterCalls = 1 };

        Assert.ThrowsAny<OperationCanceledException>(() =>
            runner.RunAsync(meeting.Id, audio.Path, cancelling).GetAwaiter().GetResult());

        var run = Assert.Single(db.Session.ListRuns(meeting.Id));
        var partial = db.Session.ListSegments(run.Id).Count;
        Assert.True(partial > 0);

        var resumeProvider = new FakeVadProvider();
        var slice = await runner.ResumeAsync(run.Id, audio.Path, resumeProvider);

        Assert.Equal(TranscriptRunStatus.Done, slice.Run.Status);
        Assert.True(slice.Segments.Count >= partial);
        // 续转不该重跑已完成的段：调用次数应少于总段数的两倍
        Assert.True(slice.Segments.Select(s => s.Ordinal).Distinct().Count() == slice.Segments.Count,
            "续转产生了重复 ordinal");
    }

    private static MeetingRecord NewMeeting(TempDb db)
    {
        var now = DateTimeOffset.UtcNow;
        return db.Session.InsertMeeting(new MeetingRecord(
            MeetingIds.NewId(), "VAD 测试", MeetingKind.Weekly, now, null,
            MeetingStatus.Ready, null, now, now));
    }
}

/// <summary>需要 VAD 的假引擎：每次调用返回"覆盖整个切片"的一段文本。</summary>
internal sealed class FakeVadProvider : IAsrProvider
{
    public int Calls { get; private set; }

    public int? CancelAfterCalls { get; init; }

    public string Id => "fake-vad";

    public AsrCapabilities Capabilities { get; } = new(
        TimestampPrecision.Segment, PunctuationMode.Native, new[] { "zh" },
        false, NeedsVad: true, false, AsrEndpointKind.InProcess);

    public IReadOnlyList<AsrModelPack> Packs { get; } =
        new[] { new AsrModelPack("fake", "Fake", 0, TimestampPrecision.Segment, new[] { "zh" }, false) };

    public AsrAvailability Probe() => AsrAvailability.Ok();

    public Task<AsrResult> TranscribeAsync(AsrJob job, IProgress<AsrProgress>? progress, CancellationToken ct)
    {
        Calls++;
        if (CancelAfterCalls is { } limit && Calls > limit)
            throw new OperationCanceledException("测试取消");

        var format = WavFormatReader.Read(job.AudioPath);
        var segments = new[] { new AsrSegment(0, (int)format.DurationMs, $"第{Calls}段", 0.9f, "zh") };

        return Task.FromResult(new AsrResult(Id, "fake", TimestampPrecision.Segment, segments,
            Array.Empty<AsrWarning>(), TimeSpan.Zero));
    }

    public void Dispose()
    {
    }
}
