using SDemo.App.Meeting.Contracts;
using Whisper.net;

namespace SDemo.App.Meeting.Asr;

/// <summary>
/// whisper.cpp 主力引擎。P/Invoke 层用 Whisper.net（同一份 ggml-org whisper.cpp 原生库，
/// 但 ABI 由上游维护，避免自己手写 whisper_full_params 的结构体布局）。
/// 契约上仍然是"进程内库"，不出网、无凭证。
/// </summary>
public sealed class WhisperCppProvider : IAsrProvider, IDisposable
{
    public const string EngineId = "whisper-cpp";

    private readonly MeetingModelCatalog _catalog;
    private readonly string _modelFile;
    private readonly object _gate = new();
    private WhisperFactory? _factory;
    private bool _disposed;

    public WhisperCppProvider(MeetingModelCatalog? catalog = null, string? modelFile = null)
    {
        _catalog = catalog ?? new MeetingModelCatalog();
        _modelFile = modelFile ?? MeetingModelCatalog.WhisperTurboQ8;
    }

    public string Id => EngineId;

    public AsrCapabilities Capabilities { get; } = new(
        Timestamps: TimestampPrecision.Segment,
        Punctuation: PunctuationMode.Native,
        Languages: new[] { "zh", "en", "ja", "yue", "auto" },
        SupportsHotwords: true,
        NeedsVad: false,
        SupportsGpu: false,
        Endpoint: AsrEndpointKind.InProcess);

    public IReadOnlyList<AsrModelPack> Packs => _catalog.WhisperPacks();

    public string ModelPath => _catalog.PathOf(_modelFile);

    public AsrAvailability Probe()
    {
        if (!File.Exists(ModelPath))
        {
            return AsrAvailability.No(
                "模型未下载",
                $"缺少 {_modelFile}，放到 {_catalog.ModelDir}（约 874 MB，见 asr-runtime §4）");
        }

        return AsrAvailability.Ok();
    }

    public async Task<AsrResult> TranscribeAsync(AsrJob job, IProgress<AsrProgress>? progress, CancellationToken ct)
    {
        var availability = Probe();
        if (!availability.Available)
            throw new MeetingException(MeetingErrorCodes.AsrUnavailable, availability.Reason ?? "模型不可用");

        var started = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (!WavFormatReader.TryRead(job.AudioPath, out var info, out var error) || info is null)
            throw new MeetingException(MeetingErrorCodes.AudioUnreadable, $"音频无法解析：{error}");

        var format = info;

        if (!format.IsTarget)
        {
            throw new MeetingException(MeetingErrorCodes.AudioUnreadable,
                $"音频格式不符：{format}，需要 16 kHz / 单声道 / 16-bit");
        }

        var totalMs = Math.Max(1, info.DurationMs);
        var factory = GetOrCreateFactory();
        var language = string.IsNullOrWhiteSpace(job.LanguageHint) || job.LanguageHint == "auto"
            ? "auto"
            : job.LanguageHint;

        var builder = factory.CreateBuilder()
            .WithThreads(job.EffectiveThreads)
            .WithLanguage(language);

        if (job.EffectiveHotwords.Count > 0)
            builder = builder.WithPrompt(string.Join(" ", job.EffectiveHotwords));

        using var processor = builder.Build();
        var segments = new List<AsrSegment>();

        await using var stream = File.OpenRead(job.AudioPath);
        await foreach (var seg in processor.ProcessAsync(stream, ct).ConfigureAwait(false))
        {
            var startMs = (int)Math.Round(seg.Start.TotalMilliseconds);
            var endMs = (int)Math.Round(seg.End.TotalMilliseconds);
            var text = seg.Text?.Trim() ?? "";
            if (text.Length == 0) continue;

            segments.Add(new AsrSegment(startMs, endMs, text, null, language == "auto" ? null : language));
            progress?.Report(new AsrProgress(
                Math.Clamp((int)(endMs * 100L / totalMs), 0, 99),
                sw.Elapsed,
                "transcribing"));
        }

        sw.Stop();
        progress?.Report(new AsrProgress(100, sw.Elapsed, "done"));

        return new AsrResult(EngineId, _modelFile, TimestampPrecision.Segment, segments,
            Array.Empty<AsrWarning>(), sw.Elapsed);
    }

    private WhisperFactory GetOrCreateFactory()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _factory ??= WhisperFactory.FromPath(ModelPath);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _factory?.Dispose();
            _factory = null;
        }
    }
}
