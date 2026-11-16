using SDemo.App.Meeting.Asr;
using SDemo.App.Meeting.Core;
using SDemo.App.Meeting.Contracts;

namespace SDemo.Meeting.Tests;

public sealed class TierFallbackTests : IDisposable
{
    private readonly string _dir;
    private readonly string _srcDir;
    private readonly MeetingModelStore _store;

    public TierFallbackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meeting-b4", Guid.NewGuid().ToString("N"));
        _srcDir = Path.Combine(Path.GetTempPath(), "meeting-b4-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_srcDir);
        _store = new MeetingModelStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_srcDir, recursive: true); } catch { }
    }

    // ── 选择器 ──────────────────────────────────────────────────────────

    [Fact]
    public void 内存充足时用默认档()
    {
        var plenty = new MemorySnapshot(32L << 30, 24L << 30);

        var decision = AsrTierSelector.Choose(_store, ModelPacks.WhisperTurboQ8, plenty, _ => true);

        Assert.Equal(ModelPacks.WhisperTurboQ8, decision.Pack.Id);
        Assert.False(decision.IsFallback);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void 内存不足时降到低配档并给出说明()
    {
        InstallSmall();

        var tight = new MemorySnapshot(8L << 30, 900L * 1024 * 1024); // 900 MB 空闲，装不下 834 MB + 512 MB 余量

        var decision = AsrTierSelector.Choose(_store, ModelPacks.WhisperTurboQ8, tight);

        Assert.Equal(ModelPacks.WhisperSmallQ5, decision.Pack.Id);
        Assert.True(decision.IsFallback);
        Assert.Contains("内存不足", decision.Reason);
    }

    [Fact]
    public void 内存不足且低配档没装时返回首选档而不是静默降级()
    {
        var tight = new MemorySnapshot(8L << 30, 900L * 1024 * 1024);

        var decision = AsrTierSelector.Choose(_store, ModelPacks.WhisperTurboQ8, tight);

        Assert.Equal(ModelPacks.WhisperTurboQ8, decision.Pack.Id);
        Assert.False(decision.IsFallback);
    }

    [Fact]
    public void 内存余量判断包含运行时开销()
    {
        var required = 500L * 1024 * 1024;

        Assert.False(SystemMemory.Fits(required, new MemorySnapshot(8L << 30, 600L * 1024 * 1024)));
        Assert.True(SystemMemory.Fits(required, new MemorySnapshot(8L << 30, 1100L * 1024 * 1024)));
    }

    // ── OOM 识别 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("std::bad_alloc")]
    [InlineData("failed to allocate memory for model")]
    [InlineData("not enough memory")]
    public void 原生内存不足会被识别(string message)
    {
        Assert.True(AsrTierSelector.LooksLikeOutOfMemory(new InvalidOperationException(message)));
    }

    [Fact]
    public void 内层异常里的内存不足也会被识别()
    {
        var ex = new InvalidOperationException("加载失败", new OutOfMemoryException());
        Assert.True(AsrTierSelector.LooksLikeOutOfMemory(ex));
    }

    [Fact]
    public void 普通错误不会被误判成内存不足()
    {
        Assert.False(AsrTierSelector.LooksLikeOutOfMemory(new InvalidOperationException("模型文件损坏")));
        Assert.False(AsrTierSelector.LooksLikeOutOfMemory(null));
    }

    // ── 运行期降档 ──────────────────────────────────────────────────────

    [Fact]
    public async Task OOM_后自动切到低配档并重试()
    {
        var primary = new ScriptedProvider("primary", throwOnCall: 1, error: new OutOfMemoryException("out of memory"));
        var fallback = new ScriptedProvider("fallback");

        using var provider = new FallbackAsrProvider(primary, () => fallback);

        var result = await provider.TranscribeAsync(new AsrJob("x.wav"), null, CancellationToken.None);

        Assert.True(provider.DidFallback);
        Assert.NotNull(provider.Note);
        Assert.Contains("内存不足", provider.Note);
        Assert.Equal("fallback", result.EngineId);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task 非内存错误不降档_原样抛出()
    {
        var primary = new ScriptedProvider("primary", throwOnCall: 1, error: new InvalidOperationException("模型损坏"));
        var fallback = new ScriptedProvider("fallback");

        using var provider = new FallbackAsrProvider(primary, () => fallback);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.TranscribeAsync(new AsrJob("x.wav"), null, CancellationToken.None));

        Assert.False(provider.DidFallback);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task 降档说明会写进_segment_的告警()
    {
        using var db = new TempDb();
        var now = DateTimeOffset.UtcNow;
        var meeting = db.Session.InsertMeeting(new MeetingRecord(
            MeetingIds.NewId(), "降档测试", MeetingKind.Weekly, now, null, MeetingStatus.Ready, null, now, now));

        var wav = TestAudio.WriteSilence(Path.Combine(db.Dir, "a.wav"), seconds: 2);
        var audio = new MeetingService(db.Session).AttachAudio(meeting.Id, AudioSource.Microphone, wav);

        using var provider = new FallbackAsrProvider(
            new ScriptedProvider("primary"),
            () => new ScriptedProvider("fallback"),
            plannedNote: "内存不足，已回落到 small",
            alreadyFallenBack: true);

        var runner = new TranscriptionRunner(new MeetingService(db.Session));
        var slice = await runner.RunAsync(meeting.Id, audio.Path, provider);

        Assert.Contains(slice.Segments, s => s.Warnings.Contains(AsrWarningCode.EngineFallback));
    }

    private void InstallSmall()
    {
        var path = Path.Combine(_srcDir, "ggml-small-q5_1.bin");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)7, 4096).ToArray());
        _store.Import(path, ModelPacks.WhisperSmallQ5);
    }
}

/// <summary>按调用次序抛错的假引擎。</summary>
internal sealed class ScriptedProvider : IAsrProvider
{
    private readonly int _throwOnCall;
    private readonly Exception? _error;

    public ScriptedProvider(string id, int throwOnCall = 0, Exception? error = null)
    {
        Id = id;
        _throwOnCall = throwOnCall;
        _error = error;
    }

    public int Calls { get; private set; }

    public string Id { get; }

    public AsrCapabilities Capabilities { get; } = new(
        TimestampPrecision.Segment, PunctuationMode.Native, new[] { "zh" },
        false, false, false, AsrEndpointKind.InProcess);

    public IReadOnlyList<AsrModelPack> Packs { get; } =
        new[] { new AsrModelPack("fake", "Fake", 0, TimestampPrecision.Segment, new[] { "zh" }, false) };

    public AsrAvailability Probe() => AsrAvailability.Ok();

    public Task<AsrResult> TranscribeAsync(AsrJob job, IProgress<AsrProgress>? progress, CancellationToken ct)
    {
        Calls++;
        if (_error is not null && Calls == _throwOnCall) throw _error;

        var segments = new[] { new AsrSegment(0, 1000, $"{Id}-文本", 0.9f, "zh") };
        return Task.FromResult(new AsrResult(Id, "fake", TimestampPrecision.Segment, segments,
            Array.Empty<AsrWarning>(), TimeSpan.Zero));
    }

    public void Dispose()
    {
    }
}
