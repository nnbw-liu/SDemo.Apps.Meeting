using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

/// <summary>
/// 转写编排：引擎无关。负责 run 生命周期、异常归类、取消语义；
/// 引擎只负责"音频进、段出去"。
/// </summary>
public sealed class TranscriptionRunner
{
    private readonly MeetingService _meetings;

    public TranscriptionRunner(MeetingService meetings) => _meetings = meetings;

    public async Task<TranscriptSlice> RunAsync(
        string meetingId,
        string audioPath,
        IAsrProvider provider,
        string? language = null,
        string? audioAssetId = null,
        IProgress<AsrProgress>? progress = null,
        CancellationToken ct = default)
    {
        var availability = provider.Probe();
        if (!availability.Available)
        {
            var detail = string.IsNullOrWhiteSpace(availability.Detail) ? "" : $"（{availability.Detail}）";
            throw new MeetingException(MeetingErrorCodes.AsrUnavailable,
                $"引擎 {provider.Id} 不可用：{availability.Reason}{detail}");
        }

        var job = new AsrJob(audioPath, language);
        var run = _meetings.BeginRun(
            meetingId,
            provider.Id,
            provider.Packs.FirstOrDefault()?.Id ?? "unknown",
            MeetingJson.Serialize(new { language, threads = job.EffectiveThreads }),
            audioAssetId);

        try
        {
            var result = await provider.TranscribeAsync(job, progress, ct).ConfigureAwait(false);
            return _meetings.CompleteRun(run, result);
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
}
