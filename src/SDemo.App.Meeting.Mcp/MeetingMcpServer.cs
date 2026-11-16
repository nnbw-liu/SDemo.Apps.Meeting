using System.Text.Json;
using System.Text.Json.Nodes;
using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;

namespace SDemo.App.Meeting.Mcp;

/// <summary>
/// MCP Streamable HTTP 的 JSON-RPC 核心（传输在 Host 的 McpHttpServer 里）。
/// 只实现工具面需要的子集：initialize / ping / tools/list / tools/call，其余返回 -32601。
/// </summary>
public sealed class MeetingMcpServer
{
    public const string ProtocolVersion = "2025-06-18";

    private static readonly string[] KnownProtocolVersions =
        ["2024-11-05", "2025-03-26", "2025-06-18"];

    private readonly MeetingQuery _query;
    private readonly MeetingService _meetings;

    public MeetingMcpServer(MeetingQuery query, MeetingService meetings)
    {
        _query = query;
        _meetings = meetings;
    }

    /// <summary>处理一条 JSON-RPC 消息；返回空字符串表示"只有通知、无需响应"。</summary>
    public string HandleJson(string body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error：请求体不是合法 JSON").ToJsonString();
        }

        if (root is JsonArray array)
        {
            var results = new JsonArray();
            foreach (var item in array)
            {
                if (HandleSingle(item) is { } response) results.Add(response);
            }

            return results.Count == 0 ? string.Empty : results.ToJsonString();
        }

        return HandleSingle(root)?.ToJsonString() ?? string.Empty;
    }

    private JsonObject? HandleSingle(JsonNode? node)
    {
        if (node is not JsonObject request)
            return Error(null, -32600, "Invalid Request：不是 JSON-RPC 对象");

        var id = request["id"]?.DeepClone();
        var methodNode = request["method"];
        if (methodNode is null || methodNode.GetValueKind() != JsonValueKind.String)
            return id is null ? null : Error(id, -32600, "Invalid Request：缺少 method");

        var method = methodNode.GetValue<string>();
        var isNotification = id is null;

        try
        {
            switch (method)
            {
                case "initialize":
                    return isNotification ? null : Result(id, InitializeResult(request["params"]));

                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;

                case "ping":
                    return isNotification ? null : Result(id, new JsonObject());

                case "tools/list":
                    return isNotification ? null : Result(id, ToolsListResult());

                case "tools/call":
                    return isNotification ? null : Result(id, CallTool(request["params"] as JsonObject));

                default:
                    return isNotification ? null : Error(id, -32601, $"Method not found：{method}");
            }
        }
        catch (MeetingException ex)
        {
            return isNotification ? null : Error(id, -32000, $"{ex.Code}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return isNotification ? null : Error(id, -32603, $"Internal error：{ex.GetType().Name}");
        }
    }

    // ── 方法实现 ────────────────────────────────────────────────────────

    private static JsonObject InitializeResult(JsonNode? parameters)
    {
        var requested = parameters?["protocolVersion"]?.GetValue<string>();
        var version = requested is not null && KnownProtocolVersions.Contains(requested)
            ? requested
            : ProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = MeetingConstants.AppId,
                ["version"] = "0.1.0",
            },
        };
    }

    private static JsonObject ToolsListResult()
    {
        var tools = new JsonArray();
        foreach (var tool in MeetingToolCatalog.Tools) tools.Add(tool.ToJson());
        return new JsonObject { ["tools"] = tools };
    }

    private JsonObject CallTool(JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>()
                   ?? throw new MeetingException(MeetingErrorCodes.InvalidArgument, "tools/call 缺少 name");
        var args = parameters?["arguments"] as JsonObject ?? new JsonObject();

        var payload = name switch
        {
            "meeting_list" => ListMeetings(args),
            "meeting_get" => GetMeeting(args),
            "meeting_transcript" => GetTranscript(args),
            "meeting_runs" => ListRuns(args),
            _ => throw new MeetingException(MeetingErrorCodes.InvalidArgument, $"未知工具：{name}"),
        };

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                },
            },
            ["isError"] = false,
        };
    }

    private JsonObject ListMeetings(JsonObject args)
    {
        var limit = Clamp(GetInt(args, "limit", 20), 1, 200);
        var since = GetDateTime(args, "since");

        var items = _query.List(limit)
            .Where(i => since is null || i.Meeting.StartAt >= since)
            .Select(i => new JsonObject
            {
                ["id"] = i.Meeting.Id,
                ["title"] = i.Meeting.Title,
                ["startAt"] = i.Meeting.StartAt.ToString("O"),
                ["status"] = i.Meeting.Status.ToString().ToLowerInvariant(),
                ["uri"] = i.Meeting.Uri,
                ["segmentCount"] = i.SegmentCount,
                ["hasTranscript"] = i.SegmentCount > 0,
                ["engine"] = i.Engine,
            });

        var array = new JsonArray();
        foreach (var item in items) array.Add(item);
        return new JsonObject { ["meetings"] = array };
    }

    private JsonObject GetMeeting(JsonObject args)
    {
        var id = RequireString(args, "id");
        var detail = _query.GetDetail(id)
                     ?? throw new MeetingException(MeetingErrorCodes.MeetingNotFound, $"会议不存在：{id}");

        var audio = new JsonArray();
        foreach (var asset in detail.Audio)
        {
            audio.Add(new JsonObject
            {
                ["source"] = asset.Source.ToString().ToLowerInvariant(),
                ["durationMs"] = asset.DurationMs,
                ["sampleRate"] = asset.SampleRate,
            });
        }

        return new JsonObject
        {
            ["id"] = detail.Meeting.Id,
            ["title"] = detail.Meeting.Title,
            ["kind"] = detail.Meeting.Kind.ToString().ToLowerInvariant(),
            ["startAt"] = detail.Meeting.StartAt.ToString("O"),
            ["endAt"] = detail.Meeting.EndAt?.ToString("O"),
            ["status"] = detail.Meeting.Status.ToString().ToLowerInvariant(),
            ["uri"] = detail.Meeting.Uri,
            ["audio"] = audio,
            ["currentRun"] = detail.Current is { } slice
                ? new JsonObject
                {
                    ["runId"] = slice.Run.Id,
                    ["engine"] = slice.Run.Engine,
                    ["model"] = slice.Run.Model,
                    ["status"] = slice.Run.Status.ToString().ToLowerInvariant(),
                    ["segmentCount"] = slice.Segments.Count,
                }
                : null,
        };
    }

    private JsonObject GetTranscript(JsonObject args)
    {
        var id = RequireString(args, "id");
        var detail = _query.GetDetail(id)
                     ?? throw new MeetingException(MeetingErrorCodes.MeetingNotFound, $"会议不存在：{id}");

        var runId = GetString(args, "runId");
        var slice = runId is null
            ? detail.Current
            : _meetings.RequireSlice(runId);

        if (slice is null)
            return new JsonObject { ["id"] = id, ["segments"] = new JsonArray(), ["notice"] = "这场会议还没有转写结果" };

        var fromMs = GetInt(args, "fromMs", 0);
        var toMs = GetInt(args, "toMs", int.MaxValue);
        var limit = Clamp(GetInt(args, "limit", 200), 1, 500);

        var segments = slice.Segments
            .Where(s => s.EndMs >= fromMs && s.StartMs <= toMs)
            .Take(limit)
            .Select(s => new JsonObject
            {
                ["ordinal"] = s.Ordinal,
                ["startMs"] = s.StartMs,
                ["endMs"] = s.EndMs,
                ["text"] = s.Text,
                ["speaker"] = s.Speaker,
                ["language"] = s.Language,
                ["warnings"] = new JsonArray(s.Warnings.Select(w => (JsonNode)w.ToString().ToLowerInvariant()).ToArray()),
            });

        var array = new JsonArray();
        foreach (var s in segments) array.Add(s);

        return new JsonObject
        {
            ["id"] = id,
            ["runId"] = slice.Run.Id,
            ["engine"] = slice.Run.Engine,
            ["totalSegments"] = slice.Segments.Count,
            ["returned"] = array.Count,
            ["segments"] = array,
        };
    }

    private JsonObject ListRuns(JsonObject args)
    {
        var id = RequireString(args, "id");
        var detail = _query.GetDetail(id)
                     ?? throw new MeetingException(MeetingErrorCodes.MeetingNotFound, $"会议不存在：{id}");

        var runs = new JsonArray();
        foreach (var run in detail.Runs)
        {
            runs.Add(new JsonObject
            {
                ["runId"] = run.Id,
                ["engine"] = run.Engine,
                ["model"] = run.Model,
                ["status"] = run.Status.ToString().ToLowerInvariant(),
                ["startedAt"] = run.StartedAt.ToString("O"),
                ["finishedAt"] = run.FinishedAt?.ToString("O"),
                ["isCurrent"] = run.Id == detail.Meeting.CurrentRunId,
            });
        }

        return new JsonObject { ["id"] = id, ["runs"] = runs };
    }

    // ── 参数与响应 ──────────────────────────────────────────────────────

    private static string RequireString(JsonObject args, string name) =>
        GetString(args, name) ?? throw new MeetingException(MeetingErrorCodes.InvalidArgument, $"缺少参数 {name}");

    private static string? GetString(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static int GetInt(JsonObject args, string name, int fallback) =>
        args[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;

    private static DateTimeOffset? GetDateTime(JsonObject args, string name)
    {
        var text = GetString(args, name);
        return text is not null && DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private static JsonObject Result(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id ?? JsonValue.Create<object?>(null),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
