using System.Text.Json.Nodes;
using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;
using SDemo.App.Meeting.Mcp;

namespace SDemo.Meeting.Tests;

public sealed class McpServerTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly MeetingMcpServer _mcp;
    private readonly string _meetingId;
    private readonly string _runId;

    public McpServerTests()
    {
        var now = DateTimeOffset.UtcNow;
        var meeting = _db.Session.InsertMeeting(new MeetingRecord(
            MeetingIds.NewId(), "周会 · 部署方案", MeetingKind.Weekly, now, null,
            MeetingStatus.Ready, null, now, now));
        _meetingId = meeting.Id;

        var run = new TranscriptRunRecord(
            MeetingIds.NewId(), meeting.Id, "whisper-cpp", "ggml-large-v3-turbo-q8_0.bin", "{}",
            TranscriptRunStatus.Done, null, now, now, null, null);
        _runId = run.Id;
        _db.Session.InsertRun(run);

        _db.Session.InsertSegments(run.Id, new[]
        {
            Segment(run.Id, 0, 0, 1200, "今天我们先过一下部署方案"),
            Segment(run.Id, 1, 1200, 2600, "现在的问题是全量切换的回滚时间"),
            Segment(run.Id, 2, 2600, 4000, "我建议先灰度两周"),
        });
        _db.Session.SetCurrentRun(meeting.Id, run.Id);

        _mcp = new MeetingMcpServer(new MeetingQuery(_db.Session), new MeetingService(_db.Session));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void initialize_返回协议版本与工具能力()
    {
        var response = Call("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");

        Assert.Equal("2025-06-18", response!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(response["result"]!["capabilities"]!["tools"]);
        Assert.Equal(MeetingConstants.AppId, response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void 未识别的协议版本会回落到当前版本()
    {
        var response = Call("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");
        Assert.Equal(MeetingMcpServer.ProtocolVersion, response!["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void tools_list_带风险元数据且全部只读()
    {
        var response = Call("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = response!["result"]!["tools"]!.AsArray();

        Assert.Equal(4, tools.Count);
        foreach (var tool in tools)
        {
            Assert.True(tool!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
            Assert.Equal("low", tool["_meta"]!["sdemo/risk"]!.GetValue<string>());
            Assert.Equal("none", tool["_meta"]!["sdemo/approval"]!.GetValue<string>());
            Assert.False(tool["_meta"]!["sdemo/writes"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void meeting_list_返回会议与段数()
    {
        var response = Call("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"meeting_list","arguments":{}}}""");
        var payload = PayloadOf(response);

        var meetings = payload["meetings"]!.AsArray();
        var first = Assert.Single(meetings)!;
        Assert.Equal(_meetingId, first["id"]!.GetValue<string>());
        Assert.Equal(3, first["segmentCount"]!.GetValue<int>());
        Assert.True(first["hasTranscript"]!.GetValue<bool>());
    }

    [Fact]
    public void meeting_get_返回当前_run_摘要()
    {
        var response = Call(ToolCall(4, "meeting_get", new JsonObject { ["id"] = _meetingId }));
        var payload = PayloadOf(response);

        Assert.Equal(_meetingId, payload["id"]!.GetValue<string>());
        Assert.Equal(_runId, payload["currentRun"]!["runId"]!.GetValue<string>());
        Assert.Equal(3, payload["currentRun"]!["segmentCount"]!.GetValue<int>());
    }

    [Fact]
    public void meeting_get_未知_id_返回带错误码的_jsonrpc_错误()
    {
        var response = Call("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"meeting_get","arguments":{"id":"nope"}}}""");

        Assert.Equal(-32000, response!["error"]!["code"]!.GetValue<int>());
        Assert.Contains("MTG_MEETING_NOT_FOUND", response["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void meeting_transcript_支持时间窗与条数裁剪()
    {
        var body = ToolCall(6, "meeting_transcript", new JsonObject
        {
            ["id"] = _meetingId,
            ["fromMs"] = 1300,   // 第 0 段止于 1200，应被排除
            ["toMs"] = 2600,
            ["limit"] = 1,
        });

        var payload = PayloadOf(Call(body));

        Assert.Equal(3, payload["totalSegments"]!.GetValue<int>());
        Assert.Equal(1, payload["returned"]!.GetValue<int>());
        var segment = Assert.Single(payload["segments"]!.AsArray())!;
        Assert.Equal(1, segment["ordinal"]!.GetValue<int>());
    }

    [Fact]
    public void meeting_runs_标出当前_run()
    {
        var response = Call(ToolCall(7, "meeting_runs", new JsonObject { ["id"] = _meetingId }));

        var run = Assert.Single(PayloadOf(response)["runs"]!.AsArray())!;
        Assert.True(run["isCurrent"]!.GetValue<bool>());
        Assert.Equal("done", run["status"]!.GetValue<string>());
    }

    [Fact]
    public void 通知没有_id_时返回空串()
    {
        Assert.Equal(string.Empty, _mcp.HandleJson("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
    }

    [Fact]
    public void 未知方法返回_32601()
    {
        var response = Call("""{"jsonrpc":"2.0","id":8,"method":"resources/list"}""");
        Assert.Equal(-32601, response!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void 非法_json_返回_32700()
    {
        Assert.Equal(-32700, _mcp.HandleJson("{not json").Contains("-32700") ? -32700 : 0);
    }

    private static string ToolCall(int id, string name, JsonObject arguments) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
    }.ToJsonString();

    private JsonNode? Call(string body)
    {
        var text = _mcp.HandleJson(body);
        Assert.False(string.IsNullOrEmpty(text), "应当有响应");
        return JsonNode.Parse(text);
    }

    private static JsonNode PayloadOf(JsonNode? response)
    {
        Assert.NotNull(response);
        var text = response!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        return JsonNode.Parse(text)!;
    }

    private static TranscriptSegmentRecord Segment(string runId, int ordinal, int start, int end, string text) =>
        new(MeetingIds.NewId(), runId, ordinal, start, end, text, 0.9, "zh",
            null, SpeakerSource.None, null, Array.Empty<AsrWarningCode>());
}
