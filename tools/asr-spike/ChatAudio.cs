using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AsrSpike;

/// <summary>
/// 音频在"chat 消息"里的两种放法。它们都是 chat completions，但不是同一个形状：
///   Ollama   → /api/chat，音频 base64 放 images 字段（空 prompt）
///   llama.cpp→ /v1/chat/completions，音频放 input_audio 内容块
/// 混用会直接报错，所以做成显式枚举。
/// </summary>
public enum ChatAudioShape
{
    OllamaImages,
    OpenAiInputAudio,
    /// <summary>把 images 字段塞给 OpenAI 兼容端点（LM Studio 这种：不报错，直接丢弃音频）。</summary>
    LmsImages,
}

public sealed record ChatAudioResult(string RawJson, string Content, TimeSpan Elapsed, int PayloadBytes, int? PromptTokens);

public sealed class ChatAudioException : Exception
{
    public ChatAudioException(int statusCode, string url, string body)
        : base($"HTTP {statusCode} ← {url}\n{Trim(body)}")
    {
        StatusCode = statusCode;
        Url = url;
        Body = body;
    }

    public int StatusCode { get; }
    public string Url { get; }
    public string Body { get; }

    private static string Trim(string s) => s.Length <= 1500 ? s : s[..1500] + "…";
}

public sealed class ChatAudioClient : IDisposable
{
    private readonly HttpClient _http;

    public ChatAudioClient(TimeSpan? timeout = null)
    {
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromMinutes(15) };
    }

    /// <summary>Ollama 的原生端点挂在根路径（/api/*），baseUrl 带 /v1 时要剥掉。</summary>
    public static string OllamaRoot(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b[..^3] : b;
    }

    /// <summary>baseUrl 末尾带不带 /v1 都要能拼对，否则会得到 /v1/v1/models。</summary>
    public static string ModelsUrl(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b + "/models" : b + "/v1/models";
    }

    public static string EndpointOf(string baseUrl, ChatAudioShape shape)
    {
        var b = baseUrl.TrimEnd('/');
        return shape switch
        {
            ChatAudioShape.OllamaImages => OllamaRoot(baseUrl) + "/api/chat",
            ChatAudioShape.LmsImages => ChatCompletionsUrl(baseUrl),
            ChatAudioShape.OpenAiInputAudio => ChatCompletionsUrl(baseUrl),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    public static string ChatCompletionsUrl(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? b + "/chat/completions"
            : b + "/v1/chat/completions";
    }

    public static string BuildPayload(string model, ChatAudioShape shape, byte[]? wav, string? prompt)
    {
        var b64 = wav is null ? null : Convert.ToBase64String(wav);
        var text = prompt ?? string.Empty;

        JsonObject body = shape switch
        {
            ChatAudioShape.OllamaImages => BuildOllama(model, b64, text),
            ChatAudioShape.LmsImages => BuildOllama(model, b64, text), // 同一份 JSON，只是发往 /v1/chat/completions
            ChatAudioShape.OpenAiInputAudio => BuildOpenAi(model, b64, text),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        return body.ToJsonString();
    }

    private static JsonObject BuildOllama(string model, string? b64, string text)
    {
        var msg = new JsonObject { ["role"] = "user", ["content"] = text };
        if (b64 is not null) msg["images"] = new JsonArray { b64 };

        return new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray { msg },
            ["stream"] = false,
            ["options"] = new JsonObject { ["temperature"] = 0 },
        };
    }

    private static JsonObject BuildOpenAi(string model, string? b64, string text)
    {
        JsonArray content = b64 is null
            ? new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } }
            : new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text },
                new JsonObject
                {
                    ["type"] = "input_audio",
                    ["input_audio"] = new JsonObject { ["data"] = b64, ["format"] = "wav" },
                },
            };

        return new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } },
            ["stream"] = false,
            ["temperature"] = 0,
        };
    }

    /// <summary>静音 WAV（16 kHz / 单声道 / 16-bit）。用来探测"音频到底有没有被模型读到"。</summary>
    public static byte[] SilentWav(int seconds)
    {
        var bytes = seconds * 16000 * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write((uint)(36 + bytes));
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16u);
        w.Write((ushort)1);
        w.Write((ushort)1);
        w.Write(16000u);
        w.Write(32000u);
        w.Write((ushort)2);
        w.Write((ushort)16);
        w.Write("data"u8.ToArray());
        w.Write((uint)bytes);
        w.Write(new byte[bytes]);
        return ms.ToArray();
    }

    public async Task<ChatAudioResult> TranscribeAsync(
        string baseUrl,
        ChatAudioShape shape,
        string model,
        byte[]? wav,
        string? prompt,
        CancellationToken ct)
    {
        var url = EndpointOf(baseUrl, shape);
        var payload = BuildPayload(model, shape, wav, prompt);
        var bytes = Encoding.UTF8.GetByteCount(payload);

        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        var sw = Stopwatch.StartNew();
        using var resp = await _http.PostAsync(url, body, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
            throw new ChatAudioException((int)resp.StatusCode, url, raw);

        // 有些服务（实测 LM Studio）会用 HTTP 200 返回 {"error": "..."}，不认这个端点也不报状态码
        if (ExtractError(raw) is { } err) throw new ChatAudioException((int)resp.StatusCode, url, err);

        return new ChatAudioResult(raw, ExtractContent(raw), sw.Elapsed, bytes, ExtractPromptTokens(raw));
    }

    /// <summary>识别 {"error": "..."} 或 {"error": {"message": "..."}} 这两种"成功状态码里的失败"。</summary>
    public static string? ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("error", out var e)) return null;

            return e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Object when e.TryGetProperty("message", out var m) => m.GetString(),
                JsonValueKind.Object => e.GetRawText(),
                _ => e.GetRawText(),
            };
        }
        catch (JsonException)
        {
            return null; // 不是 JSON，让调用方按原样报错
        }
    }

    public static int? ExtractPromptTokens(string json)    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("usage", out var u) &&
                u.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var v))
                return v;
            if (doc.RootElement.TryGetProperty("prompt_eval_count", out var pe) && pe.TryGetInt32(out var v2))
                return v2;
        }
        catch (JsonException)
        {
            // 不是 JSON，交给上层报错
        }

        return null;
    }

    public async Task<string> ProbeAsync(string baseUrl, ChatAudioShape shape, CancellationToken ct)
    {
        var url = shape == ChatAudioShape.OllamaImages ? OllamaRoot(baseUrl) + "/api/tags" : ModelsUrl(baseUrl);

        using var resp = await _http.GetAsync(url, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new ChatAudioException((int)resp.StatusCode, url, raw);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            var names = models.EnumerateArray()
                .Select(m => m.TryGetProperty("name", out var n) ? n.GetString()
                    : m.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n));
            return string.Join("\n", names);
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            var names = data.EnumerateArray()
                .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n));
            return string.Join("\n", names);
        }

        return raw.Length <= 500 ? raw : raw[..500] + "…";
    }

    /// <summary>能认出 Ollama /api/chat、OpenAI chat、llama.cpp server 三种返回。</summary>
    public static string ExtractContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var mc))
                return ContentToString(mc);

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var c0 = choices[0];
                if (c0.TryGetProperty("message", out var m2) && m2.TryGetProperty("content", out var cc))
                    return ContentToString(cc);
                if (c0.TryGetProperty("text", out var t)) return t.GetString() ?? "";
            }

            if (root.TryGetProperty("response", out var r)) return r.GetString() ?? "";

            return json; // 看不出结构就原样返回，别假装成功
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string ContentToString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Array => string.Concat(e.EnumerateArray().Select(ContentToString)),
        JsonValueKind.Object when e.TryGetProperty("text", out var t) => t.GetString() ?? "",
        _ => "",
    };

    public void Dispose() => _http.Dispose();
}
