using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Asr;

/// <summary>
/// 降档装饰器：先用首选引擎；遇到"内存不够"这类失败就切到低配引擎并重试一次。
///
/// 只对 OOM 类失败生效——别的错误（模型损坏、音频读不了）切档也没用，
/// 硬切只会把真正的错误盖住。
/// </summary>
public sealed class FallbackAsrProvider : IAsrProvider, IFallbackNote
{
    private readonly Func<IAsrProvider> _fallbackFactory;
    private IAsrProvider _current;
    private IAsrProvider? _fallback;
    private bool _disposed;

    public FallbackAsrProvider(
        IAsrProvider primary,
        Func<IAsrProvider> fallbackFactory,
        string? plannedNote = null,
        bool alreadyFallenBack = false)
    {
        _current = primary;
        _fallbackFactory = fallbackFactory;
        Note = plannedNote;
        DidFallback = alreadyFallenBack;
    }

    /// <summary>降档说明：事前（内存预判）或事中（OOM 重试）二选一，null 表示没降档。</summary>
    public string? Note { get; private set; }

    public bool DidFallback { get; private set; }

    public string Id => _current.Id;

    public AsrCapabilities Capabilities => _current.Capabilities;

    public IReadOnlyList<AsrModelPack> Packs => _current.Packs;

    public AsrAvailability Probe() => _current.Probe();

    public async Task<AsrResult> TranscribeAsync(AsrJob job, IProgress<AsrProgress>? progress, CancellationToken ct)
    {
        try
        {
            return await _current.TranscribeAsync(job, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && AsrTierSelector.LooksLikeOutOfMemory(ex))
        {
            if (_fallback is null)
            {
                _fallback = _fallbackFactory()
                            ?? throw new MeetingException(MeetingErrorCodes.AsrUnavailable,
                                "内存不足，且没有可用的低配档；请关闭其他程序，或用 `meeting models --import` 装上 small 档");

                if (!_fallback.Probe().Available)
                {
                    throw new MeetingException(MeetingErrorCodes.AsrUnavailable,
                        $"内存不足（{ex.Message}），低配档也不可用：{_fallback.Probe().Reason}");
                }
            }

            var failed = _current;
            _current = _fallback;
            DidFallback = true;
            Note = $"转写时内存不足（{Brief(ex.Message)}），已切到低配档重试";
            failed.Dispose();

            progress?.Report(new AsrProgress(0, TimeSpan.Zero, "fallback"));
            return await _fallback.TranscribeAsync(job, progress, ct).ConfigureAwait(false);
        }
    }

    private static string Brief(string message) =>
        message.Length <= 80 ? message : message[..80] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _current.Dispose();
        if (!ReferenceEquals(_current, _fallback)) _fallback?.Dispose();
    }
}
