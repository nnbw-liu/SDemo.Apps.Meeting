using System.Text.Json.Nodes;

namespace SDemo.App.Meeting.Mcp;

public enum ToolRisk
{
    Low,
    Medium,
    High,
}

/// <summary>
/// 一个 MCP 工具的完整声明。风险等级由应用声明，Agent 只按等级执行、不硬编码工具名
/// （docs/integration.md §4、hub-protocol「风险与审批」）。
/// </summary>
public sealed record McpToolDefinition(
    string Name,
    string Description,
    string InputSchemaJson,
    ToolRisk Risk,
    bool ReadOnly,
    bool Destructive = false,
    bool Idempotent = true)
{
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["description"] = Description,
        ["inputSchema"] = JsonNode.Parse(InputSchemaJson),
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = ReadOnly,
            ["destructiveHint"] = Destructive,
            ["idempotentHint"] = Idempotent,
        },
        ["_meta"] = new JsonObject
        {
            ["sdemo/risk"] = Risk.ToString().ToLowerInvariant(),
            ["sdemo/approval"] = Risk == ToolRisk.Low ? "none" : "once",
            ["sdemo/writes"] = !ReadOnly,
        },
    };
}

/// <summary>
/// v1 只暴露只读工具。写入面（标说话人、建会议、转写触发）留到 MCP 写路径与审计就位之后，
/// 避免工具面在审批/审计没落地前先膨胀（integration.md X-12）。
/// </summary>
public static class MeetingToolCatalog
{
    public static IReadOnlyList<McpToolDefinition> Tools { get; } = new[]
    {
        new McpToolDefinition(
            "meeting_list",
            "列出会议（按开始时间倒序）。返回 id / 标题 / 时间 / 状态 / 段数。",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "limit":{"type":"integer","minimum":1,"maximum":200,"default":20},
              "since":{"type":"string","format":"date-time","description":"只要这之后的会议（ISO-8601，带时区）"}
            }}
            """,
            ToolRisk.Low,
            ReadOnly: true),

        new McpToolDefinition(
            "meeting_get",
            "取一场会议：会议头 + 音频 + 当前转写 run 的摘要（不含正文）。",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "id":{"type":"string","description":"会议 id"}
            },"required":["id"]}
            """,
            ToolRisk.Low,
            ReadOnly: true),

        new McpToolDefinition(
            "meeting_transcript",
            "取某次转写的片段。支持时间窗与条数裁剪，避免一次性把整场塞进上下文。",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "id":{"type":"string","description":"会议 id"},
              "runId":{"type":"string","description":"可选，默认当前 run"},
              "fromMs":{"type":"integer","minimum":0},
              "toMs":{"type":"integer","minimum":0},
              "limit":{"type":"integer","minimum":1,"maximum":500,"default":200}
            },"required":["id"]}
            """,
            ToolRisk.Low,
            ReadOnly: true),

        new McpToolDefinition(
            "meeting_runs",
            "列出某场会议的所有转写 run（换过引擎或重跑过会有多条）。",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "id":{"type":"string","description":"会议 id"}
            },"required":["id"]}
            """,
            ToolRisk.Low,
            ReadOnly: true),

        new McpToolDefinition(
            "meeting_topics",
            "取会议议题（按序号）。每个议题带跨项目引用 refs 与决议/候选计数。",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "id":{"type":"string","description":"会议 id"}
            },"required":["id"]}
            """,
            ToolRisk.Low,
            ReadOnly: true),

        new McpToolDefinition(
            "meeting_decisions",
            "取会议决议。默认只返回 confirmed（正式纪要口径），status=all 才含 proposed。",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "id":{"type":"string","description":"会议 id"},
              "status":{"type":"string","enum":["confirmed","proposed","all"],"default":"confirmed"}
            },"required":["id"]}
            """,
            ToolRisk.Low,
            ReadOnly: true),

        new McpToolDefinition(
            "meeting_actions",
            "取待办候选。candidate 表示尚未安排；intaken 带上 plannerRef。**候选不是待办**。",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "id":{"type":"string","description":"会议 id"},
              "status":{"type":"string","enum":["candidate","intaken","rejected","all"],"default":"all"}
            },"required":["id"]}
            """,
            ToolRisk.Low,
            ReadOnly: true),

        // ── 写工具（中风险，一次确认）────────────────────────────────────
        new McpToolDefinition(
            "meeting_add_topic",
            "给会议加一个议题。refs 只接受 sdemo.* URI。",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "id":{"type":"string","description":"会议 id"},
              "title":{"type":"string","maxLength":200},
              "summary":{"type":"string","maxLength":2000},
              "refs":{"type":"array","items":{"type":"string"},"maxItems":16}
            },"required":["id","title"]}
            """,
            ToolRisk.Medium,
            ReadOnly: false,
            Idempotent: false),

        new McpToolDefinition(
            "meeting_update_topic",
            "改议题的标题 / 摘要 / 引用。只传要改的字段。",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "topicId":{"type":"string"},
              "title":{"type":"string","maxLength":200},
              "summary":{"type":"string","maxLength":2000},
              "refs":{"type":"array","items":{"type":"string"},"maxItems":16}
            },"required":["topicId"]}
            """,
            ToolRisk.Medium,
            ReadOnly: false),

        new McpToolDefinition(
            "meeting_add_decision",
            "加一条决议，初始状态 proposed；确认要用 meeting_confirm_decision。",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "id":{"type":"string","description":"会议 id"},
              "text":{"type":"string","maxLength":2000},
              "topicId":{"type":"string"},
              "rationale":{"type":"string","maxLength":2000},
              "owner":{"type":"string","maxLength":64},
              "anchorMs":{"type":"integer","minimum":0,"description":"引自转写的哪一刻"}
            },"required":["id","text"]}
            """,
            ToolRisk.Medium,
            ReadOnly: false,
            Idempotent: false),

        new McpToolDefinition(
            "meeting_confirm_decision",
            "确认决议（proposed → confirmed）。幂等：已确认时直接返回。",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "decisionId":{"type":"string"}
            },"required":["decisionId"]}
            """,
            ToolRisk.Medium,
            ReadOnly: false),

        new McpToolDefinition(
            "meeting_add_action",
            "加一条待办候选（candidate）。**不创建待办**，转待办要经 Agent 调 Planner。",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "id":{"type":"string","description":"会议 id"},
              "text":{"type":"string","maxLength":2000},
              "topicId":{"type":"string"},
              "owner":{"type":"string","maxLength":64},
              "dueAt":{"type":"string","format":"date-time"},
              "anchorMs":{"type":"integer","minimum":0}
            },"required":["id","text"]}
            """,
            ToolRisk.Medium,
            ReadOnly: false,
            Idempotent: false),

        new McpToolDefinition(
            "meeting_mark_action",
            "改候选状态。转 intaken 必须带 plannerRef（sdemo.planner/...），否则拒绝。",
            """
            {"type":"object","additionalProperties":false,"properties":{
              "actionId":{"type":"string"},
              "status":{"type":"string","enum":["candidate","intaken","rejected"]},
              "plannerRef":{"type":"string","description":"sdemo.planner/todo/..."}
            },"required":["actionId","status"]}
            """,
            ToolRisk.Medium,
            ReadOnly: false),
    };

    /// <summary>写工具名单：MCP 层据此决定要不要落审计。</summary>
    public static IReadOnlySet<string> WriteToolNames { get; } = Tools
        .Where(t => !t.ReadOnly)
        .Select(t => t.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
