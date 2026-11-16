using System.IO;
using SDemo.App.Meeting.Asr;
using SDemo.App.Meeting.Audio;
using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;

namespace SDemo.App.Meeting.Host;

/// <summary>把 <see cref="DualTrackRecorder"/> 适配成 Core 认识的采集接口。</summary>
internal sealed class DualTrackCapture : IAudioCapture
{
    private readonly DualTrackRecorder _recorder;

    public DualTrackCapture(DualTrackRecorder recorder) => _recorder = recorder;

    public void Start(bool includeLoopback) => _recorder.Start(includeLoopback);

    public CaptureResult Stop()
    {
        var recording = _recorder.Stop();

        var tracks = recording.Tracks
            .Select(t => new CapturedTrack(t.Source, t.TargetPath, (int)t.Duration.TotalMilliseconds))
            .ToArray();

        return new CaptureResult(tracks, recording.StoppedAt - recording.StartedAt);
    }

    public void Dispose() => _recorder.Dispose();
}

internal sealed class DualTrackCaptureFactory : IAudioCaptureFactory
{
    private readonly string _recordingsRoot;

    public DualTrackCaptureFactory(string recordingsRoot) => _recordingsRoot = recordingsRoot;

    public bool HasCaptureDevice => DualTrackRecorder.HasCaptureDevice();

    public IAudioCapture Create(string meetingId, string title)
    {
        var dir = Path.Combine(_recordingsRoot, meetingId);
        return new DualTrackCapture(new DualTrackRecorder(dir, "audio"));
    }
}
