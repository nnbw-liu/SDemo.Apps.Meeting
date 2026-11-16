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
    private readonly MeetingDomainService _domain;
    private readonly MeetingAuditLog _audit;
    private readonly AnalysisQueueService _analysis;

    public MeetingMcpServer(
        MeetingQuery query,
        MeetingService meetings,
        MeetingDomainService? domain = null,
        MeetingAuditLog? audit = null)
    {
        _query = query;
        _meetings = meetings;
        _domain = domain ?? new MeetingDomainService(query.Session);
        _audit = audit ?? new MeetingAuditLog(query.Session);
        _analysis = new AnalysisQueueService(query.Session);
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

        if (MeetingToolCatalog.WriteToolNames.Contains(name))
            return CallWriteTool(name, args);

        var payload = name switch
        {
            "meeting_list" => ListMeetings(args),
            "meeting_get" => GetMeeting(args),
            "meeting_transcript" => GetTranscript(args),
            "meeting_runs" => ListRuns(args),
            "meeting_topics" => ListTopics(args),
            "meeting_decisions" => ListDecisions(args),
            "meeting_actions" => ListActions(args),
            "meeting_analysis_status" => AnalysisStatus(args),
            _ => throw new MeetingException(MeetingErrorCodes.InvalidArgument, $"未知工具：{name}"),
        };

        return Envelope(payload);
    }

    /// <summary>写工具：成功与失败都落审计（失败带错误码），参数只存截断摘要。</summary>
    private JsonObject CallWriteTool(string name, JsonObject args)
    {
        var entityType = name switch
        {
            "meeting_add_topic" or "meeting_update_topic" => "topic",
            "meeting_add_decision" or "meeting_confirm_decision" => "decision",
            "meeting_request_analysis" or "meeting_claim_analysis" or "meeting_finish_analysis" => "analysis",
            _ => "action",
        };

        try
        {
            var (payload, action, entityId, meetingId) = name switch
            {
                "meeting_add_topic" => AddTopic(args),
                "meeting_update_topic" => UpdateTopic(args),
                "meeting_add_decision" => AddDecision(args),
                "meeting_confirm_decision" => ConfirmDecision(args),
                "meeting_add_action" => AddAction(args),
                "meeting_mark_action" => MarkAction(args),
                "meeting_request_analysis" => RequestAnalysis(args),
                "meeting_claim_analysis" => ClaimAnalysis(args),
                "meeting_finish_analysis" => FinishAnalysis(args),
                _ => throw new MeetingException(MeetingErrorCodes.InvalidArgument, $"未知写工具：{name}"),
            };

            _audit.Write(name, action, entityType, entityId, meetingId, args.ToJsonString());
            return Envelope(payload);
        }
        catch (MeetingException ex)
        {
            var entityId = GetString(args, "actionId") ?? GetString(args, "decisionId") ?? GetString(args, "topicId");
            _audit.WriteFailure(name, "error", entityType, $"{ex.Code}: {ex.Message}",
                entityId: entityId, meetingId: ResolveMeetingId(args), argsJson: args.ToJsonString());
            throw;
        }
    }

    /// <summary>审计要能按会议查，所以失败时也要尽量把会议 id 解出来（args 里可能只有实体 id）。</summary>
    private string? ResolveMeetingId(JsonObject args)
    {
        if (GetString(args, "id") is { } meetingId) return meetingId;

        var session = _query.Session;
        if (GetString(args, "actionId") is { } actionId) return session.GetAction(actionId)?.MeetingId;
        if (GetString(args, "decisionId") is { } decisionId) return session.GetDecision(decisionId)?.MeetingId;
        if (GetString(args, "topicId") is { } topicId) return session.GetTopic(topicId)?.MeetingId;

        return null;
    }

    private static JsonObject Envelope(JsonObject payload) => new()
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

    // ── 写工具实现 ──────────────────────────────────────────────────────

    private (JsonObject Payload, string Action, string EntityId, string MeetingId) AddTopic(JsonObject args)
    {
        var meetingId = RequireString(args, "id");
        var title = RequireString(args, "title");

        var topic = _domain.AddTopic(meetingId, title, GetString(args, "summary"), GetStringArray(args, "refs"));

        return (TopicJson(topic), "create", topic.Id, meetingId);
    }

    private (JsonObject Payload, string Action, string EntityId, string MeetingId) UpdateTopic(JsonObject args)
    {
        var topicId = RequireString(args, "topicId");

        var topic = _domain.UpdateTopic(
            topicId,
            GetString(args, "title"),
            GetString(args, "summary"),
            args.ContainsKey("refs") ? GetStringArray(args, "refs") : null);

        return (TopicJson(topic), "update", topic.Id, topic.MeetingId);
    }

    private (JsonObject Payload, string Action, string EntityId, string MeetingId) AddDecision(JsonObject args)
    {
        var meetingId = RequireString(args, "id");
        var text = RequireString(args, "text");

        var decision = _domain.AddDecision(
            meetingId,
            text,
            GetString(args, "topicId"),
            GetString(args, "rationale"),
            GetString(args, "owner"),
            args["anchorMs"] is JsonValue v && v.TryGetValue<int>(out var anchor) ? anchor : null);

        return (DecisionJson(decision), "create", decision.Id, meetingId);
    }

    private (JsonObject Payload, string Action, string EntityId, string MeetingId) ConfirmDecision(JsonObject args)
    {
        var decisionId = RequireString(args, "decisionId");

        var decision = _domain.ConfirmDecision(decisionId);

        return (DecisionJson(decision), "confirm", decision.Id, decision.MeetingId);
    }

    private (JsonObject Payload, string Action, string EntityId, string MeetingId) AddAction(JsonObject args)
    {
        var meetingId = RequireString(args, "id");
        var text = RequireString(args, "text");

        var action = _domain.AddAction(
            meetingId,
            text,
            GetString(args, "topicId"),
            GetString(args, "owner"),
            GetDateTime(args, "dueAt"),
            args["anchorMs"] is JsonValue v && v.TryGetValue<int>(out var anchor) ? anchor : null);

        return (ActionJson(action), "create", action.Id, meetingId);
    }

    private (JsonObject Payload, string Action, string EntityId, string MeetingId) MarkAction(JsonObject args)
    {
        var actionId = RequireString(args, "actionId");
        var statusText = RequireString(args, "status");

        if (!Enum.TryParse<ActionStatus>(statusText, ignoreCase: true, out var status))
            throw new MeetingException(MeetingErrorCodes.InvalidArgument,
                $"status 只能是 candidate / intaken / rejected：{statusText}");

        var updated = _domain.MarkAction(actionId, status, GetString(args, "plannerRef"));

        return (ActionJson(updated), "mark", updated.Id, updated.MeetingId);
    }

    // ── 分析队列 ────────────────────────────────────────────────────────

    private JsonObject AnalysisStatus(JsonObject args)
    {
        var id = RequireString(args, "id");
        var job = _analysis.Latest(id);

        return new JsonObject
        {
            ["meetingId"] = id,
            ["hasJob"] = job.Id.Length > 0,
            ["job"] = job.Id.Length == 0 ? null : AnalysisJson(job),
        };
    }

    private (JsonObject Payload, string Action, string EntityId, string MeetingId) RequestAnalysis(JsonObject args)
    {
        var meetingId = RequireString(args, "id");
        var job = _analysis.Request(meetingId, AnalysisQueueService.RequestedByAgent, GetString(args, "promptVersion"));

        return (AnalysisJson(job), "request", job.Id, meetingId);
    }

    private (JsonObject Payload, string Action, string EntityId, string MeetingId) ClaimAnalysis(JsonObject args)
    {
        var job = _analysis.Claim();

        return job is null
            ? (new JsonObject { ["job"] = null, ["notice"] = "队列里没有待分析的会议" }, "claim", string.Empty, string.Empty)
            : (AnalysisJson(job), "claim", job.Id, job.MeetingId);
    }

    private (JsonObject Payload, string Action, string EntityId, string MeetingId) FinishAnalysis(JsonObject args)
    {
        var jobId = RequireString(args, "jobId");
        var error = GetString(args, "error");

        string action;
        AnalysisJob job;

        if (!string.IsNullOrWhiteSpace(error))
        {
            job = _analysis.Fail(jobId, error);
            action = "fail";
        }
        else
        {
            var segmentsDone = args["segmentsDone"] is JsonValue s && s.TryGetValue<int>(out var sd) ? sd : -1;
            if (segmentsDone >= 0) _analysis.Progress(jobId, segmentsDone);

            job = _analysis.Complete(
                jobId,
                GetInt(args, "topics", 0),
                GetInt(args, "decisions", 0),
                GetInt(args, "actions", 0),
                GetString(args, "provider"));
            action = "complete";
        }

        return (AnalysisJson(job), action, job.Id, job.MeetingId);
    }

    private static JsonObject AnalysisJson(AnalysisJob job) => new()
    {
        ["id"] = job.Id,
        ["meetingId"] = job.MeetingId,
        ["status"] = job.Status.ToString().ToLowerInvariant(),
        ["requestedBy"] = job.RequestedBy,
        ["provider"] = job.Provider,
        ["promptVersion"] = job.PromptVersion,
        ["segmentsTotal"] = job.SegmentsTotal,
        ["segmentsDone"] = job.SegmentsDone,
        ["topics"] = job.TopicsCreated,
        ["decisions"] = job.DecisionsCreated,
        ["actions"] = job.ActionsCreated,
        ["error"] = job.Error,
        ["createdAt"] = job.CreatedAt.ToString("O"),
        ["startedAt"] = job.StartedAt?.ToString("O"),
        ["finishedAt"] = job.FinishedAt?.ToString("O"),
    };

    // ── 实体 JSON ───────────────────────────────────────────────────────

    private static JsonObject TopicJson(TopicRecord topic) => new()
    {
        ["id"] = topic.Id,
        ["meetingId"] = topic.MeetingId,
        ["ordinal"] = topic.Ordinal,
        ["title"] = topic.Title,
        ["summary"] = topic.Summary,
        ["refs"] = new JsonArray(topic.Refs.Select(r => (JsonNode)r).ToArray()),
    };

    private static JsonObject DecisionJson(DecisionRecord decision) => new()
    {
        ["id"] = decision.Id,
        ["meetingId"] = decision.MeetingId,
        ["topicId"] = decision.TopicId,
        ["text"] = decision.Text,
        ["rationale"] = decision.Rationale,
        ["owner"] = decision.Owner,
        ["status"] = decision.Status.ToString().ToLowerInvariant(),
        ["anchorMs"] = decision.AnchorMs,
    };

    private static JsonObject ActionJson(ActionRecord action) => new()
    {
        ["id"] = action.Id,
        ["meetingId"] = action.MeetingId,
        ["topicId"] = action.TopicId,
        ["text"] = action.Text,
        ["owner"] = action.Owner,
        ["dueAt"] = action.DueAt?.ToString("O"),
        ["status"] = action.Status.ToString().ToLowerInvariant(),
        ["plannerRef"] = action.PlannerRef,
        ["anchorMs"] = action.AnchorMs,
    };

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
            ["domain"] = DomainCounts(detail.Meeting.Id),
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

    private JsonObject DomainCounts(string meetingId)
    {
        var domain = _domain.Get(meetingId);
        return new JsonObject
        {
            ["topics"] = domain.Topics.Count,
            ["decisions"] = domain.Decisions.Count,
            ["confirmedDecisions"] = domain.ConfirmedDecisions,
            ["actions"] = domain.Actions.Count,
            ["candidateActions"] = domain.CandidateActions,
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

    private JsonObject ListTopics(JsonObject args)
    {
        var id = RequireString(args, "id");
        var domain = _domain.Get(id);

        var topics = new JsonArray();
        foreach (var topic in domain.Topics)
        {
            var decisions = domain.Decisions.Count(d => d.TopicId == topic.Id);
            var actions = domain.Actions.Count(a => a.TopicId == topic.Id);

            topics.Add(new JsonObject
            {
                ["id"] = topic.Id,
                ["ordinal"] = topic.Ordinal,
                ["title"] = topic.Title,
                ["summary"] = topic.Summary,
                ["refs"] = new JsonArray(topic.Refs.Select(r => (JsonNode)r).ToArray()),
                ["decisionCount"] = decisions,
                ["actionCount"] = actions,
            });
        }

        return new JsonObject { ["id"] = id, ["topics"] = topics };
    }

    private JsonObject ListDecisions(JsonObject args)
    {
        var id = RequireString(args, "id");
        var domain = _domain.Get(id);
        var status = GetString(args, "status") ?? "confirmed";

        var filtered = status.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? domain.Decisions
            : domain.Decisions.Where(d => d.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase)).ToArray();

        var array = new JsonArray();
        foreach (var decision in filtered)
        {
            array.Add(new JsonObject
            {
                ["id"] = decision.Id,
                ["topicId"] = decision.TopicId,
                ["text"] = decision.Text,
                ["rationale"] = decision.Rationale,
                ["owner"] = decision.Owner,
                ["status"] = decision.Status.ToString().ToLowerInvariant(),
                ["anchorMs"] = decision.AnchorMs,
            });
        }

        return new JsonObject
        {
            ["id"] = id,
            ["status"] = status,
            ["total"] = domain.Decisions.Count,
            ["returned"] = array.Count,
            ["decisions"] = array,
        };
    }

    private JsonObject ListActions(JsonObject args)
    {
        var id = RequireString(args, "id");
        var domain = _domain.Get(id);
        var status = GetString(args, "status") ?? "all";

        var filtered = status.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? domain.Actions
            : domain.Actions.Where(a => a.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase)).ToArray();

        var array = new JsonArray();
        foreach (var action in filtered)
        {
            array.Add(new JsonObject
            {
                ["id"] = action.Id,
                ["topicId"] = action.TopicId,
                ["text"] = action.Text,
                ["owner"] = action.Owner,
                ["dueAt"] = action.DueAt?.ToString("O"),
                ["status"] = action.Status.ToString().ToLowerInvariant(),
                ["plannerRef"] = action.PlannerRef,
                ["anchorMs"] = action.AnchorMs,
            });
        }

        return new JsonObject
        {
            ["id"] = id,
            ["status"] = status,
            ["total"] = domain.Actions.Count,
            ["candidates"] = domain.CandidateActions,
            ["returned"] = array.Count,
            ["actions"] = array,
        };
    }

    private JsonObject ListRuns(JsonObject args)    {
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

    private static IReadOnlyList<string>? GetStringArray(JsonObject args, string name) =>
        args[name] is JsonArray array
            ? array.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToArray()
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
