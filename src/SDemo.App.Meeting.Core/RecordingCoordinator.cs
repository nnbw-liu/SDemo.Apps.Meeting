using System.Diagnostics;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

/// <summary>
/// 把「录音 → 落库 → 转写」串成一个可被界面驱动的流程。
///
/// 三件事由这里负责：
/// 1. 计时（界面 1 Hz 刷新，不做假动画）；
/// 2. 停止后自动挂 audio asset 并转写，进度回推给界面；
/// 3. 取消要真的停掉采集与转写，不留半截 run。
///
/// 采集设备与 ASR provider 都是注入的：前者让状态机能在无麦克风环境测试，
/// 后者让"模型没装"这类失败走同一条错误路径。
/// </summary>
public sealed class RecordingCoordinator : IRecorderController, IDisposable
{
    private readonly MeetingService _meetings;
    private readonly Storage.MeetingSession _session;
    private readonly IAudioCaptureFactory _captures;
    private readonly Func<IAsrProvider> _providerFactory;
    private readonly string _recordingsRoot;

    private readonly object _gate = new();
    private RecordingSnapshot _snapshot = RecordingSnapshot.Idle;
    private IAudioCapture? _capture;
    private CancellationTokenSource? _runCts;
    private Timer? _ticker;
    private DateTimeOffset? _startedAt;

    public RecordingCoordinator(
        MeetingService meetings,
        Storage.MeetingSession session,
        IAudioCaptureFactory captures,
        Func<IAsrProvider> providerFactory,
        string? recordingsRoot = null)
    {
        _meetings = meetings;
        _session = session;
        _captures = captures;
        _providerFactory = providerFactory;
        _recordingsRoot = recordingsRoot
                          ?? Path.Combine(MeetingConstants.ResolveDataDir(), MeetingConstants.RecordingsDirName);
    }

    public RecordingSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public bool CanRecord => _captures.HasCaptureDevice;

    public event Action<RecordingSnapshot>? Changed;

    public Task<RecordingSnapshot> StartAsync(RecordingOptions options)
    {
        if (Snapshot.IsBusy) return Task.FromResult(Snapshot);

        if (!CanRecord)
        {
            return Task.FromResult(Publish(new RecordingSnapshot(
                RecordingPhase.Failed,
                ErrorCode: MeetingErrorCodes.NoAudioDevice,
                Message: "本机没有可用的录音设备。检查麦克风是否接好，并在 Windows「设置 → 隐私和安全性 → 麦克风」里允许桌面应用访问。")));
        }

        var title = string.IsNullOrWhiteSpace(options.Title)
            ? $"会议 {DateTimeOffset.Now:MM-dd HH:mm}"
            : options.Title.Trim();

        try
        {
            var meeting = _meetings.CreateMeeting(title);
            var capture = _captures.Create(meeting.Id, title);

            try
            {
                capture.Start(options.IncludeLoopback);
            }
            catch
            {
                // 设备起不来就别留一个空会议在那儿（用户会看到一条"未转写"的幽灵记录）
                capture.Dispose();
                try { _meetings.SoftDeleteMeeting(meeting.Id); } catch { /* 清理是尽力而为 */ }
                throw;
            }

            lock (_gate)
            {
                _capture = capture;
                _startedAt = DateTimeOffset.UtcNow;
            }

            _ticker?.Dispose();
            _ticker = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

            return Task.FromResult(Publish(new RecordingSnapshot(
                RecordingPhase.Recording,
                meeting.Id,
                meeting.Title,
                TimeSpan.Zero,
                Message: "正在录音…")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Publish(new RecordingSnapshot(
                RecordingPhase.Failed,
                Message: DescribeRecordingFailure(ex),
                ErrorCode: MeetingErrorCodes.NoAudioDevice)));
        }
    }

    public async Task<RecordingSnapshot> StopAsync()
    {
        IAudioCapture? capture;
        string? meetingId;
        string? title;

        lock (_gate)
        {
            capture = _capture;
            meetingId = _snapshot.MeetingId;
            title = _snapshot.MeetingTitle;
        }

        if (capture is null || meetingId is null) return Snapshot;

        _ticker?.Dispose();
        _ticker = null;

        CaptureResult result;
        try
        {
            result = capture.Stop();
            capture.Dispose();
        }
        catch (Exception ex)
        {
            lock (_gate) _capture = null;
            return Publish(new RecordingSnapshot(RecordingPhase.Failed, meetingId, title,
                Message: $"停止录音失败：{ex.Message}"));
        }

        lock (_gate) _capture = null;

        var tracks = new List<RecordedTrackInfo>();
        foreach (var track in result.Tracks)
        {
            var asset = _meetings.AttachAudio(meetingId, track.Source, track.Path);
            tracks.Add(new RecordedTrackInfo(track.Source, track.Path, (int)asset.DurationMs));
        }

        if (tracks.Count == 0)
        {
            return Publish(new RecordingSnapshot(RecordingPhase.Failed, meetingId, title,
                Tracks: tracks, Message: "没有录到任何音频"));
        }

        Publish(new RecordingSnapshot(RecordingPhase.Transcribing, meetingId, title,
            Progress: 0, Tracks: tracks, Message: "转写中…"));

        _runCts = new CancellationTokenSource();
        var progress = new Progress<AsrProgress>(p =>
            Publish(Snapshot with { Phase = RecordingPhase.Transcribing, Progress = p.Percent / 100.0 }));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var provider = _providerFactory();

            // 双轨先转人声主轨（环回单独一趟，不混音——这是铁律）
            var primary = result.Microphone ?? result.Tracks[0];
            var assetId = _session.ListAudio(meetingId).FirstOrDefault(a => a.Path == primary.Path)?.Id;

            var runner = new TranscriptionRunner(_meetings);
            await runner.RunAsync(
                meetingId,
                primary.Path,
                provider,
                new TranscriptionOptions(),
                audioAssetId: assetId,
                progress: progress,
                ct: _runCts.Token);

            return Publish(new RecordingSnapshot(RecordingPhase.Done, meetingId, title,
                stopwatch.Elapsed, 1, tracks,
                Message: $"转写完成（{stopwatch.Elapsed:mm\\:ss}），可以看了"));
        }
        catch (OperationCanceledException)
        {
            return Publish(new RecordingSnapshot(RecordingPhase.Idle, meetingId, title,
                stopwatch.Elapsed, null, tracks, Message: "已取消转写（已录的段落保留，可续转）"));
        }
        catch (Exception ex)
        {
            return Publish(new RecordingSnapshot(RecordingPhase.Failed, meetingId, title,
                stopwatch.Elapsed, null, tracks,
                Message: $"转写失败：{DescribeTranscriptionFailure(ex)}",
                ErrorCode: (ex as MeetingException)?.Code));
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    public Task<RecordingSnapshot> CancelAsync()
    {
        _runCts?.Cancel();

        IAudioCapture? capture;
        lock (_gate) capture = _capture;

        if (capture is null) return Task.FromResult(Snapshot);

        _ticker?.Dispose();
        _ticker = null;
        lock (_gate) _capture = null;

        try
        {
            capture.Stop();
        }
        catch
        {
            // 取消路径不抛
        }
        finally
        {
            capture.Dispose();
        }

        return Task.FromResult(Publish(new RecordingSnapshot(
            RecordingPhase.Idle,
            Snapshot.MeetingId,
            Snapshot.MeetingTitle,
            Message: "已取消录音（音轨丢弃，会议条目保留）")));
    }

    private void Tick()
    {
        DateTimeOffset? started;
        lock (_gate) started = _startedAt;
        if (started is null) return;

        var snapshot = Snapshot;
        if (snapshot.Phase != RecordingPhase.Recording) return;

        Publish(snapshot with { Elapsed = DateTimeOffset.UtcNow - started.Value });
    }

    private RecordingSnapshot Publish(RecordingSnapshot snapshot)
    {
        lock (_gate) _snapshot = snapshot;
        Changed?.Invoke(snapshot);
        return snapshot;
    }

    private static string DescribeRecordingFailure(Exception ex)
    {
        var message = ex.Message;

        // 0x80070005：Windows 麦克风隐私拒绝——最常见的一种，必须给出可照做的指引
        if (message.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)
            || message.Contains("拒绝访问", StringComparison.Ordinal))
        {
            return "Windows 拒绝了麦克风访问。到「设置 → 隐私和安全性 → 麦克风」里允许桌面应用使用麦克风，然后重试。";
        }

        return $"无法开始录音：{message}";
    }

    private static string DescribeTranscriptionFailure(Exception ex) => ex switch
    {
        MeetingException m => m.Message,
        _ => ex.Message,
    };

    public void Dispose()
    {
        _ticker?.Dispose();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _capture?.Dispose();
    }
}
