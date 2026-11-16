using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;

namespace SDemo.Meeting.Tests;

/// <summary>
/// 录音状态机：用假采集 + 假引擎跑完「开始 → 停止 → 自动转写」，不需要真麦克风。
/// 这也是 P1 唯一能在无麦克风机器上自动验证的路径。
/// </summary>
public sealed class RecordingTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly string _wav;

    public RecordingTests()
    {
        _wav = TestAudio.WriteSilence(Path.Combine(_db.Dir, "mic.wav"), seconds: 2);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task 开始录音会建会议并进入录音态()
    {
        var (coordinator, _) = Create();

        var snapshot = await coordinator.StartAsync(new RecordingOptions(Title: "周会"));

        Assert.Equal(RecordingPhase.Recording, snapshot.Phase);
        Assert.Equal("周会", snapshot.MeetingTitle);
        Assert.NotNull(snapshot.MeetingId);
        Assert.Single(_db.Session.ListMeetings());
    }

    [Fact]
    public async Task 没有采集设备时给出可照做的提示()
    {
        var (coordinator, _) = Create(hasDevice: false);

        var snapshot = await coordinator.StartAsync(new RecordingOptions());

        Assert.Equal(RecordingPhase.Failed, snapshot.Phase);
        Assert.Equal(MeetingErrorCodes.NoAudioDevice, snapshot.ErrorCode);
        Assert.Contains("麦克风", snapshot.Message);
        Assert.Empty(_db.Session.ListMeetings());
    }

    [Fact]
    public async Task 麦克风权限被拒时把_0x80070005_翻译成人话()
    {
        var (coordinator, factory) = Create();
        factory.ThrowOnStart = new InvalidOperationException("打开麦克风失败：0x80070005 拒绝访问");

        var snapshot = await coordinator.StartAsync(new RecordingOptions());

        Assert.Equal(RecordingPhase.Failed, snapshot.Phase);
        Assert.Contains("设置 → 隐私和安全性 → 麦克风", snapshot.Message);
        Assert.DoesNotContain("0x80070005", snapshot.Message);
    }

    [Fact]
    public async Task 设备起不来不会留下空会议()
    {
        var (coordinator, factory) = Create();
        factory.ThrowOnStart = new InvalidOperationException("打开麦克风失败：0x80070005 拒绝访问");

        await coordinator.StartAsync(new RecordingOptions(Title: "本该失败的会"));

        // 幽灵条目会让用户以为"有会没转写"，所以失败时要把刚建的会议软删掉
        Assert.Empty(_db.Session.ListMeetings());
    }

    [Fact]
    public async Task 停止后自动转写并落段()
    {
        var (coordinator, _) = Create();

        await coordinator.StartAsync(new RecordingOptions(Title: "自动转写"));
        var snapshot = await coordinator.StopAsync();

        Assert.Equal(RecordingPhase.Done, snapshot.Phase);
        Assert.Equal(1, snapshot.Progress);

        var meetingId = snapshot.MeetingId!;
        var slice = new MeetingService(_db.Session).GetCurrentTranscript(meetingId);
        Assert.NotNull(slice);
        Assert.Equal(2, slice!.Segments.Count);
        Assert.Equal("第一段", slice.Segments[0].Text);
        Assert.Single(_db.Session.ListAudio(meetingId));
    }

    [Fact]
    public async Task 停止时没录到音轨就报错()
    {
        var (coordinator, _) = Create(tracks: Array.Empty<CapturedTrack>());

        await coordinator.StartAsync(new RecordingOptions());
        var snapshot = await coordinator.StopAsync();

        Assert.Equal(RecordingPhase.Failed, snapshot.Phase);
        Assert.Contains("没有录到任何音频", snapshot.Message);
    }

    [Fact]
    public async Task 模型没就绪时失败但不丢录音()
    {
        var (coordinator, _) = Create(provider: () => throw new MeetingException(
            MeetingErrorCodes.AsrUnavailable, "还没有可用的转写模型。"));

        await coordinator.StartAsync(new RecordingOptions());
        var snapshot = await coordinator.StopAsync();

        Assert.Equal(RecordingPhase.Failed, snapshot.Phase);
        Assert.Equal(MeetingErrorCodes.AsrUnavailable, snapshot.ErrorCode);
        Assert.Contains("转写模型", snapshot.Message);

        // 音轨已经挂上，用户装上模型还能续转
        Assert.Single(_db.Session.ListAudio(snapshot.MeetingId!));
    }

    [Fact]
    public async Task 取消录音会丢弃音轨但留下会议条目()
    {
        var (coordinator, _) = Create();

        var started = await coordinator.StartAsync(new RecordingOptions());
        var snapshot = await coordinator.CancelAsync();

        Assert.Equal(RecordingPhase.Idle, snapshot.Phase);
        Assert.Contains("已取消", snapshot.Message);
        Assert.Empty(_db.Session.ListAudio(started.MeetingId!));
        Assert.Single(_db.Session.ListMeetings());
    }

    [Fact]
    public async Task 录音中重复开始不会开第二场()
    {
        var (coordinator, _) = Create();

        await coordinator.StartAsync(new RecordingOptions(Title: "第一场"));
        var second = await coordinator.StartAsync(new RecordingOptions(Title: "第二场"));

        Assert.Equal(RecordingPhase.Recording, second.Phase);
        Assert.Equal("第一场", second.MeetingTitle);
        Assert.Single(_db.Session.ListMeetings());
    }

    // ── 装配 ────────────────────────────────────────────────────────────

    private (IRecorderController Coordinator, FakeCaptureFactory Factory) Create(
        bool hasDevice = true,
        IReadOnlyList<CapturedTrack>? tracks = null,
        Func<IAsrProvider>? provider = null)
    {
        var factory = new FakeCaptureFactory(hasDevice, tracks ?? new[] { new CapturedTrack(AudioSource.Microphone, _wav, 2000) });
        var coord = new RecordingCoordinator(
            new MeetingService(_db.Session),
            _db.Session,
            factory,
            provider ?? (() => new FakeAsr()),
            Path.Combine(_db.Dir, "recordings"));

        return (coord, factory);
    }

    private sealed class FakeCaptureFactory(bool hasDevice, IReadOnlyList<CapturedTrack> tracks) : IAudioCaptureFactory
    {
        public Exception? ThrowOnStart { get; set; }

        public bool HasCaptureDevice => hasDevice;

        public IAudioCapture Create(string meetingId, string title) => new FakeCapture(this, tracks);
    }

    private sealed class FakeCapture(FakeCaptureFactory owner, IReadOnlyList<CapturedTrack> tracks) : IAudioCapture
    {
        public void Start(bool includeLoopback)
        {
            if (owner.ThrowOnStart is { } ex) throw ex;
        }

        public CaptureResult Stop() => new(tracks, TimeSpan.FromSeconds(2));

        public void Dispose()
        {
        }
    }

    private sealed class FakeAsr : IAsrProvider
    {
        public string Id => "fake";

        public AsrCapabilities Capabilities { get; } = new(
            TimestampPrecision.Segment,
            PunctuationMode.Native,
            new[] { "zh" },
            SupportsHotwords: false,
            NeedsVad: false,
            SupportsGpu: false,
            AsrEndpointKind.InProcess);

        public IReadOnlyList<AsrModelPack> Packs => Array.Empty<AsrModelPack>();

        public AsrAvailability Probe() => AsrAvailability.Ok();

        public Task<AsrResult> TranscribeAsync(AsrJob job, IProgress<AsrProgress>? progress, CancellationToken ct)
        {
            progress?.Report(new AsrProgress(100, TimeSpan.FromMilliseconds(10)));

            return Task.FromResult(new AsrResult(
                Id,
                "fake-model",
                TimestampPrecision.Segment,
                new[]
                {
                    new AsrSegment(0, 1000, "第一段", Language: "zh"),
                    new AsrSegment(1000, 2000, "第二段", Language: "zh"),
                },
                Array.Empty<AsrWarning>(),
                TimeSpan.FromMilliseconds(10)));
        }

        public void Dispose()
        {
        }
    }
}
