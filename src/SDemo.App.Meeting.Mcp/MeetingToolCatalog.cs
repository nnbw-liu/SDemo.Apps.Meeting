using System.Text.Json;

namespace SDemo.App.Meeting.Mcp;

public enum ToolRisk
{
    Low,
    Medium,
    High,
}

public sealed record MeetingTool(
    string Name,
    string Description,
    ToolRisk Risk,
    bool ReadOnly,
    string InputSchemaJson);

/// <summary>
/// MCP 工具目录。v1 只暴露只读工具；写入面（mark_action / create）留到后续阶段，
/// 免得工具面在需求没稳之前先膨胀（integration.md X-12）。
/// </summary>
public static class MeetingToolCatalog
{
    public static IReadOnlyList<MeetingTool> Tools { get; } = new[]
    {
        new MeetingTool(
            "meeting_list",
            "列出会议（按开始时间倒序）。返回 id / 标题 / 时间 / 状态 / 是否有转写。",
            ToolRisk.Low,
            true,
            """
            {"type":"object","properties":{
              "limit":{"type":"integer","minimum":1,"maximum":200,"default":20},
              "since":{"type":"string","description":"ISO 时间，只要这之后的会议"}
            }}
            """),

        new MeetingTool(
            "meeting_get",
            "取一场会议：会议头 + 当前转写 run + 议题/决议的占位结构。",
            ToolRisk.Low,
            true,
            """
            {"type":"object","properties":{
              "id":{"type":"string","description":"会议 id"}
            },"required":["id"]}
            """),

        new MeetingTool(
            "meeting_transcript",
            "取某次转写的片段。可按时间窗裁剪，避免一次性把整场塞进上下文。",
            ToolRisk.Low,
            true,
            """
            {"type":"object","properties":{
              "id":{"type":"string","description":"会议 id"},
              "runId":{"type":"string","description":"可选，默认当前 run"},
              "fromMs":{"type":"integer"},
              "toMs":{"type":"integer"},
              "limit":{"type":"integer","minimum":1,"maximum":500,"default":200}
            },"required":["id"]}
            """),

        new MeetingTool(
            "meeting_runs",
            "列出某场会议的所有转写 run（换过引擎或重跑过会有多条）。",
            ToolRisk.Low,
            true,
            """
            {"type":"object","properties":{
              "id":{"type":"string","description":"会议 id"}
            },"required":["id"]}
            """),
    };

    public static string ToJsonArray()
    {
        var payload = Tools.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            risk = t.Risk.ToString().ToLowerInvariant(),
            readOnly = t.ReadOnly,
            inputSchema = JsonDocument.Parse(t.InputSchemaJson).RootElement,
        });

        return JsonSerializer.Serialize(payload);
    }
}
