using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Asr;

/// <summary>
/// DeepSeek 独立语音识别接口：<c>POST /v1/tools/asr</c>，multipart 上传 <c>file</c>，
/// 返回 <c>{request_id, text, duration_ms}</c>——**纯文本，没有时间戳、没有语种参数**。
///
/// 不是 OpenAI 兼容形状（既不是 /v1/audio/transcriptions，也不是 chat + input_audio：
/// 实测 chat/completions 会 422，因为 deepseek-flash 的输入模态是 text + image）。
///
/// 硬限制（官方文档）：≤ 8 MiB、≤ 120 秒、16-bit PCM WAV 或 Ogg Opus。
/// 所以它只能吃"已经切好的片段"——VAD 分段是它的前置条件。
/// </summary>
public sealed class DeepSeekToolsProvider : IAsrProvider
{
    public const string EngineId = "deepseek-tools";
    public const string DefaultBaseUrl = "https://api.deepseek.com";

    /// <summary>官方上限：8 MiB。</summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>官方上限：120 秒。</summary>
    public const long MaxDurationMs = 120_000;

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public DeepSeekToolsProvider(string? baseUrl = null, string? apiKey = null, bool allowRemote = true)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new MeetingException(MeetingErrorCodes.InvalidArgument,
                "deepseek-tools 需要 API Key（--api-key 或 SDEMO_ASR_API_KEY）");

        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        var loopback = Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;
        if (!loopback && !allowRemote)
            throw new MeetingException(MeetingErrorCodes.InvalidArgument,
                $"deepseek-tools 端点不是回环地址：{_baseUrl}。云端端点必须显式加 --allow-remote（音频会上传）。");

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public string Id => EngineId;

    public AsrCapabilities Capabilities { get; } = new(
        Timestamps: TimestampPrecision.Segment,
        Punctuation: PunctuationMode.Native,
        Languages: new[]
        {
            "zh", "yue", "en", "ja", "ko", "de", "fr", "es", "pt", "it", "ru",
            "ar", "hi", "id", "vi", "th", "tr", "ms", "fil", "pl", "hu",
        },
        SupportsHotwords: false,
        NeedsVad: true,
        SupportsGpu: false,
        Endpoint: AsrEndpointKind.HttpRemote);

    public IReadOnlyList<AsrModelPack> Packs { get; } = Array.Empty<AsrModelPack>();

    public string Endpoint => _baseUrl + "/v1/tools/asr";

    public AsrAvailability Probe()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var resp = _http.GetAsync(_baseUrl + "/v1/models", cts.Token).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode
                ? AsrAvailability.Ok()
                : AsrAvailability.No("端点返回非 2xx", $"{(int)resp.StatusCode} {_baseUrl}/v1/models");
        }
        catch (Exception ex)
        {
            return AsrAvailability.No("端点不可达", $"{_baseUrl}：{ex.Message}");
        }
    }

    public async Task<AsrResult> TranscribeAsync(AsrJob job, IProgress<AsrProgress>? progress, CancellationToken ct)
    {
        if (!WavFormatReader.TryRead(job.AudioPath, out var info, out var error) || info is null)
            throw new MeetingException(MeetingErrorCodes.AudioUnreadable, $"音频无法解析：{error}");

        if (!info.IsTarget)
            throw new MeetingException(MeetingErrorCodes.AudioUnreadable,
                $"音频格式不符：{info}，需要 16 kHz / 单声道 / 16-bit");

        var bytes = await File.ReadAllBytesAsync(job.AudioPath, ct).ConfigureAwait(false);

        if (bytes.Length > MaxBytes)
            throw new MeetingException(MeetingErrorCodes.AudioUnreadable,
                $"音频过大：{bytes.Length / 1024d / 1024d:F1} MB，deepseek-tools 上限 8 MB；请先切段");

        if (info.DurationMs > MaxDurationMs)
            throw new MeetingException(MeetingErrorCodes.AudioUnreadable,
                $"音频过长：{info.DurationMs / 1000.0:F0} 秒，deepseek-tools 上限 120 秒；请先切段");

        var sw = Stopwatch.StartNew();
        progress?.Report(new AsrProgress(10, sw.Elapsed, "uploading"));

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", Path.GetFileName(job.AudioPath));

        using var resp = await _http.PostAsync(Endpoint, form, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new MeetingException(MeetingErrorCodes.AsrFailed, $"HTTP {(int)resp.StatusCode}：{Trim(raw)}");

        progress?.Report(new AsrProgress(90, sw.Elapsed, "parsing"));
        var parsed = ParseResponse(raw);

        var segment = new AsrSegment(0, (int)parsed.DurationMs, parsed.Text, null, null);
        var warnings = parsed.Text.Length == 0
            ? new[] { new AsrWarning(AsrWarningCode.EmptyOutput, 0, "引擎返回空文本") }
            : Array.Empty<AsrWarning>();

        progress?.Report(new AsrProgress(100, sw.Elapsed, "done"));

        return new AsrResult(EngineId, "deepseek-asr", TimestampPrecision.Segment,
            new[] { segment }, warnings, sw.Elapsed);
    }

    public sealed record DeepSeekAsrResponse(string RequestId, string Text, long DurationMs);

    public static DeepSeekAsrResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var requestId = root.TryGetProperty("request_id", out var r) ? r.GetString() ?? "" : "";
        var duration = root.TryGetProperty("duration_ms", out var d) && d.TryGetInt64(out var v) ? v : 0;

        return new DeepSeekAsrResponse(requestId, text.Trim(), duration);
    }

    private static string Trim(string s) => s.Length <= 400 ? s : s[..400] + "…";

    public void Dispose() => _http.Dispose();
}
