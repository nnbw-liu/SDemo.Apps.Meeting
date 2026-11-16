using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Asr;

public enum ChatAudioShape
{
    /// <summary>llama.cpp server：/v1/chat/completions + input_audio 内容块。</summary>
    OpenAiInputAudio,

    /// <summary>Ollama：/api/chat + images 字段。</summary>
    OllamaImages,
}

/// <summary>
/// 升级档引擎：本机回环的 chat-audio 端点（llama.cpp server / Ollama）。
/// 三条自收紧纪律（asr-runtime §4.5）：
///   1. endpoint 只接受 loopback；
///   2. 只发固定提示 + 音频，不接受任意 messages；
///   3. 只吐 AsrSegment，不暴露原始 completion。
/// </summary>
public sealed partial class ChatAudioProvider : IAsrProvider, IDisposable
{
    public const string EngineId = "chat-audio";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly ChatAudioShape _shape;

    public ChatAudioProvider(string baseUrl, string model, ChatAudioShape shape = ChatAudioShape.OpenAiInputAudio)
    {
        if (!IsLoopback(baseUrl))
            throw new MeetingException(MeetingErrorCodes.InvalidArgument, $"chat-audio 端点必须是回环地址：{baseUrl}");

        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _shape = shape;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
    }

    public string Id => EngineId;

    public AsrCapabilities Capabilities { get; } = new(
        Timestamps: TimestampPrecision.Segment,
        Punctuation: PunctuationMode.Native,
        Languages: new[] { "zh", "en", "ja", "auto" },
        SupportsHotwords: false,
        NeedsVad: true,
        SupportsGpu: true,
        Endpoint: AsrEndpointKind.HttpLoopback);

    public IReadOnlyList<AsrModelPack> Packs { get; } = Array.Empty<AsrModelPack>();

    public string Endpoint => _shape == ChatAudioShape.OllamaImages
        ? OllamaRoot(_baseUrl) + "/api/chat"
        : ChatCompletionsUrl(_baseUrl);

    public AsrAvailability Probe()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var resp = _http.GetAsync(ModelsUrl(_baseUrl), cts.Token).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode
                ? AsrAvailability.Ok()
                : AsrAvailability.No("端点返回非 2xx", $"{(int)resp.StatusCode} {Endpoint}");
        }
        catch (Exception ex)
        {
            return AsrAvailability.No("端点不可达", $"{_baseUrl}：{ex.Message}");
        }
    }

    public async Task<AsrResult> TranscribeAsync(AsrJob job, IProgress<AsrProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var format = WavFormatReader.TryRead(job.AudioPath, out var info, out var error)
            ? info!
            : throw new MeetingException(MeetingErrorCodes.AudioUnreadable, $"音频无法解析：{error}");

        var bytes = await File.ReadAllBytesAsync(job.AudioPath, ct).ConfigureAwait(false);
        var payload = BuildPayload(_model, _shape, bytes);

        progress?.Report(new AsrProgress(5, sw.Elapsed, "uploading"));
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new MeetingException(MeetingErrorCodes.AsrFailed, $"HTTP {(int)resp.StatusCode}：{Trim(raw)}");

        var parsed = ParseResponse(raw);
        progress?.Report(new AsrProgress(95, sw.Elapsed, "parsing"));

        var segment = new AsrSegment(0, (int)Math.Max(0, info.DurationMs), parsed.Text, null, parsed.Language);
        var warnings = parsed.Text.Length == 0
            ? new[] { new AsrWarning(AsrWarningCode.EmptyOutput, 0, "引擎返回空文本（音频可能被服务丢弃）") }
            : Array.Empty<AsrWarning>();

        progress?.Report(new AsrProgress(100, sw.Elapsed, "done"));

        return new AsrResult(EngineId, _model, TimestampPrecision.Segment,
            new[] { segment }, warnings, sw.Elapsed);
    }

    // ── 载荷与解析 ──────────────────────────────────────────────────────

    public static string BuildPayload(string model, ChatAudioShape shape, byte[] wav, string? prompt = null)
    {
        var b64 = Convert.ToBase64String(wav);
        var text = prompt ?? string.Empty;

        JsonObject body = shape == ChatAudioShape.OllamaImages
            ? new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "user", ["content"] = text, ["images"] = new JsonArray { b64 } },
                },
                ["stream"] = false,
            }
            : new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = text },
                            new JsonObject
                            {
                                ["type"] = "input_audio",
                                ["input_audio"] = new JsonObject { ["data"] = b64, ["format"] = "wav" },
                            },
                        },
                    },
                },
                ["stream"] = false,
                ["temperature"] = 0,
            };

        return body.ToJsonString();
    }

    public sealed record ParsedCompletion(string Text, string? Language);

    public static ParsedCompletion ParseResponse(string rawJson)
    {
        var content = ExtractContent(rawJson);
        return new ParsedCompletion(ExtractText(content, out var lang), lang);
    }

    /// <summary>剥 <c>language X</c> 前缀与 <c>&lt;asr_text&gt;</c> 标记，去掉代码围栏与控制符。</summary>
    public static string ExtractText(string raw, out string? language)
    {
        language = null;
        var s = ControlToken().Replace((raw ?? "").Replace("\r\n", "\n").Trim(), "").Trim();

        const string tag = "<asr_text>";
        var idx = s.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        string text;

        if (idx >= 0)
        {
            var head = s[..idx];
            var m = LanguageRegex().Match(head);
            if (m.Success) language = m.Groups[1].Value.Trim();
            text = s[(idx + tag.Length)..];
        }
        else
        {
            var m = LanguageRegex().Match(s);
            if (m.Success)
            {
                language = m.Groups[1].Value.Trim();
                text = s[m.Length..];
            }
            else
            {
                text = s;
            }
        }

        text = FenceRegex().Replace(text, "").Trim();
        text = Regex.Replace(text, @"[ \t]+", " ").Trim();
        return text;
    }

    private static string ExtractContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var mc))
                return mc.GetString() ?? "";

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var m2) && m2.TryGetProperty("content", out var cc))
                    return cc.GetString() ?? "";
            }

            return json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private static string OllamaRoot(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b[..^3] : b;
    }

    private static string ChatCompletionsUrl(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b + "/chat/completions" : b + "/v1/chat/completions";
    }

    private static string ModelsUrl(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b + "/models" : b + "/v1/models";
    }

    private static string Trim(string s) => s.Length <= 400 ? s : s[..400] + "…";

    [GeneratedRegex(@"<\|[A-Za-z0-9_]+\|>")]
    private static partial Regex ControlToken();

    [GeneratedRegex(@"^\s*language\s*[:：]?\s*([A-Za-z][A-Za-z\-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageRegex();

    [GeneratedRegex(@"^```[^\n]*\n?|```\s*$")]
    private static partial Regex FenceRegex();

    public void Dispose() => _http.Dispose();
}
