namespace SDemo.App.Meeting.Contracts;

/// <summary>
/// 引擎能力声明。硬纪律：**上层只读能力，不判断引擎名**。
/// </summary>
public sealed record AsrCapabilities(
    TimestampPrecision Timestamps,
    PunctuationMode Punctuation,
    IReadOnlyList<string> Languages,
    bool SupportsHotwords,
    bool NeedsVad,
    bool SupportsGpu,
    AsrEndpointKind Endpoint)
{
    /// <summary>
    /// 能力 → 块 anchor 粒度。KB 投影按这个值决定"能否跳到那句话"。
    /// </summary>
    public AnchorGranularity Anchor =>
        Timestamps switch
        {
            TimestampPrecision.Word => AnchorGranularity.Block,
            TimestampPrecision.Segment => AnchorGranularity.Sentence,
            _ => AnchorGranularity.Document,
        };

    public bool SupportsLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || Languages.Contains(language, StringComparer.OrdinalIgnoreCase);
}

/// <summary>能被 KB anchor 表达的粒度。Document 表示"只能引用整场"。</summary>
public enum AnchorGranularity
{
    Document,
    Sentence,
    Block,
}

/// <summary>一个可下载 / 已下载的模型包。</summary>
public sealed record AsrModelPack(
    string Id,
    string DisplayName,
    long Bytes,
    TimestampPrecision Timestamps,
    IReadOnlyList<string> Languages,
    bool Optional)
{
    public double SizeGb => Bytes / 1024d / 1024d / 1024d;
}

/// <summary>一次转写任务。</summary>
public sealed record AsrJob(
    string AudioPath,
    string? LanguageHint = null,
    IReadOnlyList<string>? Hotwords = null,
    int Threads = 0)
{
    public IReadOnlyList<string> EffectiveHotwords => Hotwords ?? Array.Empty<string>();

    public int EffectiveThreads => Threads > 0 ? Threads : Math.Max(1, Environment.ProcessorCount - 2);
}

public sealed record AsrProgress(int Percent, TimeSpan Elapsed, string? Stage = null);

public sealed record AsrWord(int StartMs, int EndMs, string Text);

public sealed record AsrSegment(
    int StartMs,
    int EndMs,
    string Text,
    float? Confidence = null,
    string? Language = null,
    IReadOnlyList<AsrWord>? Words = null);

public sealed record AsrWarning(AsrWarningCode Code, int AtMs, string Detail);

public sealed record AsrResult(
    string EngineId,
    string ModelId,
    TimestampPrecision Timestamps,
    IReadOnlyList<AsrSegment> Segments,
    IReadOnlyList<AsrWarning> Warnings,
    TimeSpan Elapsed)
{
    public string? DetectPrimaryLanguage() =>
        Segments.Select(s => s.Language)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .GroupBy(l => l!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

    public long TotalMs => Segments.Count == 0 ? 0 : Segments.Max(s => s.EndMs);
}

/// <summary>
/// 转写引擎。实现不许碰数据库、不许知道会议存在——只做"音频进、段出去"。
/// 实现必须可释放：原生句柄与 HttpClient 都挂在实例上。
/// </summary>
public interface IAsrProvider : IDisposable
{
    string Id { get; }

    AsrCapabilities Capabilities { get; }

    IReadOnlyList<AsrModelPack> Packs { get; }

    /// <summary>当前引擎是否可用（二进制在、模型在）。不可用时调用方应给出明确提示而不是崩。</summary>
    AsrAvailability Probe();

    Task<AsrResult> TranscribeAsync(AsrJob job, IProgress<AsrProgress>? progress, CancellationToken ct);
}

public sealed record AsrAvailability(bool Available, string? Reason, string? Detail)
{
    public static AsrAvailability Ok() => new(true, null, null);
    public static AsrAvailability No(string reason, string? detail = null) => new(false, reason, detail);
}

/// <summary>引擎可以附带一条"运行说明"（例如降档、切 CPU），由编排层写进 warnings。</summary>
public interface IFallbackNote
{
    string? Note { get; }
}