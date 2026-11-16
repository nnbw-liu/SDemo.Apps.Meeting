using NAudio.CoreAudioApi;
using NAudio.Wave;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Audio;

public sealed record RecordedTrack(AudioSource Source, string RawPath, string TargetPath, WavFormat Format, TimeSpan Duration);

public sealed record DualRecording(IReadOnlyList<RecordedTrack> Tracks, DateTimeOffset StartedAt, DateTimeOffset StoppedAt)
{
    public RecordedTrack? Microphone => Tracks.FirstOrDefault(t => t.Source == AudioSource.Microphone);
    public RecordedTrack? Loopback => Tracks.FirstOrDefault(t => t.Source == AudioSource.Loopback);
}

/// <summary>
/// 双轨录音：麦克风（room）/ 系统环回（online）。两路各自落原始格式，
/// 停止后统一转成 16 kHz 单声道——**不先混音再转写**（meeting-design §7）。
///
/// 采集是硬件相关代码，最容易在无麦克风 / 虚拟声卡 / 隐私拒绝的环境下无限挂起。
/// 所以每一步启动都带超时：拿不到设备、或 5 秒内没有数据，都明确失败，绝不吊死调用方。
/// </summary>
public sealed class DualTrackRecorder : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FirstDataTimeout = TimeSpan.FromSeconds(5);

    private sealed class Track
    {
        public IWaveIn? Capture;
        public WaveFileWriter? Writer;
        public long Bytes;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource FirstData = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            try { Capture?.Dispose(); } catch { }
            try { Writer?.Dispose(); } catch { }
            Capture = null;
            Writer = null;
        }
    }

    private readonly string _outputDir;
    private readonly string _baseName;
    private Track? _mic;
    private Track? _loop;
    private DateTimeOffset _startedAt;
    private bool _disposed;

    public DualTrackRecorder(string outputDir, string baseName)
    {
        _outputDir = outputDir;
        _baseName = baseName;
    }

    public bool IsRecording { get; private set; }

    public string MicRawPath => Path.Combine(_outputDir, $"{_baseName}.mic.raw.wav");
    public string LoopRawPath => Path.Combine(_outputDir, $"{_baseName}.loop.raw.wav");

    public void Start(bool includeLoopback = true)
    {
        if (IsRecording) throw new InvalidOperationException("已经在录音");
        Directory.CreateDirectory(_outputDir);

        try
        {
            _mic = StartTrack(OpenMicrophone, MicRawPath, "麦克风", requireData: true);

            if (includeLoopback)
            {
                try
                {
                    _loop = StartTrack(OpenLoopback, LoopRawPath, "环回", requireData: false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[warn] 环回采集不可用，只录麦克风：{ex.Message}");
                    _loop?.Dispose();
                    _loop = null;
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }

        _startedAt = DateTimeOffset.UtcNow;
        IsRecording = true;
    }

    public DualRecording Stop()
    {
        if (!IsRecording) throw new InvalidOperationException("没有在录音");

        StopQuietly(_mic);
        StopQuietly(_loop);
        Thread.Sleep(250);

        DisposeRecorders();
        IsRecording = false;

        var stoppedAt = DateTimeOffset.UtcNow;
        var tracks = new List<RecordedTrack>();

        AddTrack(tracks, AudioSource.Microphone, MicRawPath);
        AddTrack(tracks, AudioSource.Loopback, LoopRawPath);

        if (tracks.Count == 0)
        {
            throw new MeetingException(MeetingErrorCodes.NoAudioDevice,
                "没有录到任何音频：设备已打开但没有数据（检查麦克风权限，或设备是否被别的程序独占）");
        }

        return new DualRecording(tracks, _startedAt, stoppedAt);
    }

    private static IWaveIn OpenMicrophone()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        return new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 100);
    }

    private static IWaveIn OpenLoopback() => new WasapiLoopbackCapture();

    private Track StartTrack(Func<IWaveIn> open, string path, string label, bool requireData)
    {
        var track = new Track();

        _ = Task.Run(() =>
        {
            try
            {
                track.Capture = open();
                track.Writer = new WaveFileWriter(path, track.Capture.WaveFormat);

                var writer = track.Writer;
                var first = track.FirstData;
                track.Capture.DataAvailable += (_, e) =>
                {
                    try
                    {
                        writer.Write(e.Buffer, 0, e.BytesRecorded);
                        Interlocked.Add(ref track.Bytes, e.BytesRecorded);
                        first.TrySetResult();
                    }
                    catch
                    {
                        // 写盘失败不抛进采集线程
                    }
                };

                track.Capture.StartRecording();
                track.Started.TrySetResult();
            }
            catch (Exception ex)
            {
                track.Started.TrySetException(ex);
            }
        });

        if (!WaitWithoutThrowing(track.Started.Task, StartTimeout))
        {
            track.Dispose();
            throw new MeetingException(MeetingErrorCodes.NoAudioDevice,
                $"{label}启动超时（{StartTimeout.TotalSeconds:F0} 秒）");
        }

        if (track.Started.Task.IsFaulted)
        {
            var inner = track.Started.Task.Exception?.GetBaseException();
            track.Dispose();
            throw new MeetingException(MeetingErrorCodes.NoAudioDevice,
                $"{label}打开失败：{Describe(inner)}", inner);
        }

        if (requireData && !track.FirstData.Task.Wait(FirstDataTimeout))
        {
            track.Dispose();
            throw new MeetingException(MeetingErrorCodes.NoAudioDevice,
                $"{label}已打开但 {FirstDataTimeout.TotalSeconds:F0} 秒内没有收到任何数据" +
                "（检查 Windows 麦克风隐私设置，或设备是否被独占）");
        }

        return track;
    }

    private static bool WaitWithoutThrowing(Task task, TimeSpan timeout)
    {
        try
        {
            return task.Wait(timeout);
        }
        catch (AggregateException)
        {
            return true; // 已失败，交给调用方看 IsFaulted
        }
    }

    /// <summary>把 COM / 权限类异常翻译成用户能照做的提示。</summary>
    private static string Describe(Exception? ex)
    {
        if (ex is null) return "未知错误";

        var accessDenied = ex is UnauthorizedAccessException
                           || ex.HResult == unchecked((int)0x80070005)
                           || ex.Message.Contains("0x80070005", StringComparison.OrdinalIgnoreCase);

        return accessDenied
            ? $"{ex.Message}（Windows 隐私设置拒绝了这个应用访问麦克风：设置 → 隐私和安全性 → 麦克风）"
            : ex.Message;
    }

    private void AddTrack(List<RecordedTrack> tracks, AudioSource source, string rawPath)
    {
        if (!File.Exists(rawPath) || new FileInfo(rawPath).Length <= 44) return;

        var targetPath = Path.Combine(_outputDir, $"{_baseName}.{source.ToString().ToLowerInvariant()}.wav");
        var format = WavDownmixer.ToTargetFormat(rawPath, targetPath);
        tracks.Add(new RecordedTrack(source, rawPath, targetPath, format, TimeSpan.FromMilliseconds(format.DurationMs)));
    }

    private static void StopQuietly(Track? track)
    {
        try { track?.Capture?.StopRecording(); } catch { /* 停止阶段不抛 */ }
    }

    private void DisposeRecorders()
    {
        _mic?.Dispose();
        _mic = null;
        _loop?.Dispose();
        _loop = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { DisposeRecorders(); } catch { }
    }

    /// <summary>本机是否有可用的采集设备（没有就让 CLI 给出可读提示，而不是抛 COM 异常）。</summary>
    public static bool HasCaptureDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                || enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        catch
        {
            return false;
        }
    }
}
