using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

public enum RecordingPhase
{
    Idle,
    Recording,
    Transcribing,
    Done,
    Failed,
}

/// <summary>录音 → 落库 → 转写 的一步操作参数。</summary>
public sealed record RecordingOptions(
    string? Title = null,
    bool IncludeLoopback = true,
    string? Language = null,
    IReadOnlyList<string>? Hotwords = null);

/// <summary>一条录到的音轨（给界面显示用，只暴露路径与时长）。</summary>
public sealed record RecordedTrackInfo(AudioSource Source, string Path, int DurationMs);

/// <summary>
/// 录音面板的快照。界面每次 <see cref="IRecorderController.Changed"/> 拿一份新的，
/// 不自己去推断状态机。
/// </summary>
public sealed record RecordingSnapshot(
    RecordingPhase Phase,
    string? MeetingId = null,
    string? MeetingTitle = null,
    TimeSpan Elapsed = default,
    double? Progress = null,
    IReadOnlyList<RecordedTrackInfo>? Tracks = null,
    string? Message = null,
    string? ErrorCode = null)
{
    public static RecordingSnapshot Idle { get; } = new(RecordingPhase.Idle);

    public bool IsBusy => Phase is RecordingPhase.Recording or RecordingPhase.Transcribing;

    public bool IsRecording => Phase == RecordingPhase.Recording;
}

/// <summary>
/// 界面可驱动的录音/转写控制器。抽象放在 Core（net10.0），
/// 实现在 Core 里、采集设备通过 <see cref="IAudioCaptureFactory"/> 注入，
/// 这样没有麦克风的机器也能用假实现把状态机跑完（测试）。
/// </summary>
public interface IRecorderController
{
    RecordingSnapshot Snapshot { get; }

    /// <summary>本机有没有可用采集设备（没有就给用户一句能看懂的话）。</summary>
    bool CanRecord { get; }

    event Action<RecordingSnapshot>? Changed;

    Task<RecordingSnapshot> StartAsync(RecordingOptions options);

    /// <summary>停止录音并自动转写。</summary>
    Task<RecordingSnapshot> StopAsync();

    /// <summary>取消当前录音或转写。</summary>
    Task<RecordingSnapshot> CancelAsync();
}

/// <summary>一条录到的原始音轨（WAV 已在磁盘上）。</summary>
public sealed record CapturedTrack(AudioSource Source, string Path, int DurationMs);

public sealed record CaptureResult(IReadOnlyList<CapturedTrack> Tracks, TimeSpan Duration)
{
    public CapturedTrack? Microphone => Tracks.FirstOrDefault(t => t.Source == AudioSource.Microphone);
}

/// <summary>一次录音会话。实现负责真正开设备、写 WAV；<see cref="Stop"/> 返回落盘音轨。</summary>
public interface IAudioCapture : IDisposable
{
    void Start(bool includeLoopback);

    CaptureResult Stop();
}

/// <summary>采集设备的来源。桌面实现基于 WASAPI；测试用假实现。</summary>
public interface IAudioCaptureFactory
{
    /// <summary>本机有没有可用采集设备。</summary>
    bool HasCaptureDevice { get; }

    IAudioCapture Create(string meetingId, string title);
}
