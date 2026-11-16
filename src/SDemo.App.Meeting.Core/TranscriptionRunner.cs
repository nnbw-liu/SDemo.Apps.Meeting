using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

public sealed record TranscriptionOptions(
    string? Language = null,
    IReadOnlyList<string>? Hotwords = null,
    int MaxSegmentMs = 60_000,
    bool UseVad = true,
    bool AnalyzeQuality = true)
{
    public static TranscriptionOptions Default { get; } = new();
}

/// <summary>
/// 转写编排：引擎无关。负责 run 生命周期、VAD 分段、增量落库、取消与续转。
///
/// 两条路径由 <see cref="AsrCapabilities.NeedsVad"/> 决定：
///   · whisper 这类自带长音频能力的引擎 → 整段一次调用；
///   · chat-audio / DeepSeek tools（单次 ≤120 秒、无时间戳）→ 先 VAD 切 20–60 秒，再逐段送。
/// 这也是"能力声明"存在的意义：上层按能力分流，不按引擎名写 if。
/// </summary>
public sealed class TranscriptionRunner
{
    private readonly MeetingService _meetings;

    public TranscriptionRunner(MeetingService meetings) => _meetings = meetings;

    public Task<TranscriptSlice> RunAsync(
        string meetingId,
        string audioPath,
        IAsrProvider provider,
        string? language = null,
        string? audioAssetId = null,
        IProgress<AsrProgress>? progress = null,
        CancellationToken ct = default)
        => RunAsync(meetingId, audioPath, provider,
            new TranscriptionOptions(Language: language), audioAssetId, progress, ct);

    public async Task<TranscriptSlice> RunAsync(
        string meetingId,
        string audioPath,
        IAsrProvider provider,
        TranscriptionOptions options,
        string? audioAssetId = null,
        IProgress<AsrProgress>? progress = null,
        CancellationToken ct = default)
    {
        EnsureAvailable(provider);

        var run = _meetings.BeginRun(
            meetingId,
            provider.Id,
            provider.Packs.FirstOrDefault()?.Id ?? "unknown",
            MeetingJson.Serialize(new
            {
                options.Language,
                options.Hotwords,
                options.MaxSegmentMs,
                options.UseVad,
                threads = new AsrJob(audioPath).EffectiveThreads,
            }),
            audioAssetId);

        return await ExecuteAsync(run, audioPath, provider, options, startOrdinal: 0, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 续转：从已落库的段数继续。只对"被中断/取消、且音频还在"的 run 有意义。
    /// </summary>
    public async Task<TranscriptSlice> ResumeAsync(
        string runId,
        string audioPath,
        IAsrProvider provider,
        TranscriptionOptions? options = null,
        IProgress<AsrProgress>? progress = null,
        CancellationToken ct = default)
    {
        var run = _meetings.RequireRun(runId);

        if (run.IsTerminal && run.Status != TranscriptRunStatus.Cancelled)
            throw new MeetingException(MeetingErrorCodes.RunNotTerminal,
                $"run 已是终态（{run.Status}），不能续转；请新建一次转写");

        EnsureAvailable(provider);
        _meetings.MarkRunning(run);

        var startOrdinal = _meetings.CountSegments(runId);
        return await ExecuteAsync(run, audioPath, provider, options ?? TranscriptionOptions.Default, startOrdinal, progress, ct)
            .ConfigureAwait(false);
    }

    private static void EnsureAvailable(IAsrProvider provider)
    {
        var availability = provider.Probe();
        if (!availability.Available)
        {
            var detail = string.IsNullOrWhiteSpace(availability.Detail) ? "" : $"（{availability.Detail}）";
            throw new MeetingException(MeetingErrorCodes.AsrUnavailable,
                $"引擎 {provider.Id} 不可用：{availability.Reason}{detail}");
        }
    }

    private async Task<TranscriptSlice> ExecuteAsync(
        TranscriptRunRecord run,
        string audioPath,
        IAsrProvider provider,
        TranscriptionOptions options,
        int startOrdinal,
        IProgress<AsrProgress>? progress,
        CancellationToken ct)
    {
        try
        {
            var collected = provider.Capabilities.NeedsVad && options.UseVad
                ? await RunSegmentedAsync(run, audioPath, provider, options, startOrdinal, progress, ct).ConfigureAwait(false)
                : await RunWholeAsync(run, audioPath, provider, options, startOrdinal, progress, ct).ConfigureAwait(false);

            if (options.AnalyzeQuality)
            {
                var warnings = TranscriptQuality.Analyze(collected, options.Language);
                _meetings.ApplyWarnings(run.Id, warnings);
            }

            var result = new AsrResult(provider.Id, provider.Packs.FirstOrDefault()?.Id ?? "unknown",
                provider.Capabilities.Timestamps, collected, Array.Empty<AsrWarning>(), TimeSpan.Zero);

            return _meetings.CompleteRun(run, result, collected);
        }
        catch (OperationCanceledException)
        {
            _meetings.CancelRun(run);
            throw;
        }
        catch (MeetingException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _meetings.FailRun(run, MeetingErrorCodes.AsrFailed, ex.Message);
            throw new MeetingException(MeetingErrorCodes.AsrFailed, $"转写失败：{ex.Message}", ex);
        }
    }

    private async Task<IReadOnlyList<AsrSegment>> RunWholeAsync(
        TranscriptRunRecord run,
        string audioPath,
        IAsrProvider provider,
        TranscriptionOptions options,
        int startOrdinal,
        IProgress<AsrProgress>? progress,
        CancellationToken ct)
    {
        var result = await provider
            .TranscribeAsync(BuildJob(audioPath, options), progress, ct)
            .ConfigureAwait(false);

        if (startOrdinal == 0 || _meetings.CountSegments(run.Id) == 0)
            _meetings.AppendSegments(run, result.Segments, startOrdinal);

        return result.Segments;
    }

    private async Task<IReadOnlyList<AsrSegment>> RunSegmentedAsync(
        TranscriptRunRecord run,
        string audioPath,
        IAsrProvider provider,
        TranscriptionOptions options,
        int startOrdinal,
        IProgress<AsrProgress>? progress,
        CancellationToken ct)
    {
        var samples = VadSegmenter.ReadSamples(audioPath);
        var windows = VadSegmenter.Segment(samples,
            new VadSegmenter.Options(MaxSegmentMs: options.MaxSegmentMs));

        if (windows.Count == 0)
            windows = new[] { new VadSegment(0, (int)(samples.Length * 1000.0 / MeetingConstants.TargetSampleRate)) };

        var all = new List<AsrSegment>();
        var ordinal = startOrdinal;

        for (var i = 0; i < windows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // 续转：前 N 段已经落过库，跳过
            if (i < startOrdinal) continue;

            var window = windows[i];
            var slicePath = WavSlicer.WriteTemporary(samples, window.StartMs, window.EndMs);

            try
            {
                var job = BuildJob(slicePath, options);
                var partial = await provider.TranscribeAsync(job, null, ct).ConfigureAwait(false);

                var shifted = partial.Segments
                    .Select(s => s with { StartMs = s.StartMs + window.StartMs, EndMs = s.EndMs + window.StartMs })
                    .Where(s => !string.IsNullOrWhiteSpace(s.Text))
                    .ToArray();

                if (shifted.Length > 0)
                {
                    _meetings.AppendSegments(run, shifted, ordinal);
                    ordinal += shifted.Length;
                    all.AddRange(shifted);
                }

                progress?.Report(new AsrProgress(
                    Math.Clamp((i + 1) * 100 / windows.Count, 0, 100),
                    TimeSpan.Zero,
                    $"segment {i + 1}/{windows.Count}"));
            }
            finally
            {
                TryDelete(slicePath);
            }
        }

        return Dedupe(all);
    }

    private static AsrJob BuildJob(string audioPath, TranscriptionOptions options) =>
        new(audioPath, options.Language, options.Hotwords);

    /// <summary>相邻段完全同文时丢掉后一条（VAD 补齐窗口重叠导致的重复）。</summary>
    private static IReadOnlyList<AsrSegment> Dedupe(List<AsrSegment> segments)
    {
        if (segments.Count < 2) return segments;

        var result = new List<AsrSegment> { segments[0] };
        foreach (var seg in segments.Skip(1))
        {
            var last = result[^1];
            if (string.Equals(last.Text, seg.Text, StringComparison.Ordinal)
                && seg.StartMs <= last.EndMs + 200)
            {
                continue;
            }

            result.Add(seg);
        }

        return result;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 临时文件删不掉不影响结果 */ }
    }
}
