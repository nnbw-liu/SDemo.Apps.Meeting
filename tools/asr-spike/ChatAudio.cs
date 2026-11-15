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
}

public sealed record ChatAudioResult(string RawJson, string Content, TimeSpan Elapsed, int PayloadBytes);

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

    public static string EndpointOf(string baseUrl, ChatAudioShape shape)
    {
        var b = baseUrl.TrimEnd('/');
        return shape switch
        {
            ChatAudioShape.OllamaImages => b + "/api/chat",
            ChatAudioShape.OpenAiInputAudio => b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? b + "/chat/completions"
                : b + "/v1/chat/completions",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    public static string BuildPayload(string model, ChatAudioShape shape, byte[] wav, string? prompt)
    {
        var b64 = Convert.ToBase64String(wav);
        var text = prompt ?? string.Empty;

        JsonObject body = shape switch
        {
            ChatAudioShape.OllamaImages => new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = text,
                        ["images"] = new JsonArray { b64 },
                    },
                },
                ["stream"] = false,
                ["options"] = new JsonObject { ["temperature"] = 0 },
            },

            ChatAudioShape.OpenAiInputAudio => new JsonObject
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
            },

            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        return body.ToJsonString();
    }

    public async Task<ChatAudioResult> TranscribeAsync(
        string baseUrl,
        ChatAudioShape shape,
        string model,
        byte[] wav,
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

        return new ChatAudioResult(raw, ExtractContent(raw), sw.Elapsed, bytes);
    }

    public async Task<string> ProbeAsync(string baseUrl, ChatAudioShape shape, CancellationToken ct)
    {
        var b = baseUrl.TrimEnd('/');
        var url = shape == ChatAudioShape.OllamaImages ? b + "/api/tags" : b + "/v1/models";

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
