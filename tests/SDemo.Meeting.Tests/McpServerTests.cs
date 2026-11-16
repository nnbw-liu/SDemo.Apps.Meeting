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
    public void tools_list_读工具低风险_写工具中风险且需确认()
    {
        var response = Call("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = response!["result"]!["tools"]!.AsArray();

        Assert.Equal(13, tools.Count);

        var reads = tools.Where(t => t!["annotations"]!["readOnlyHint"]!.GetValue<bool>()).ToArray();
        var writes = tools.Where(t => !t!["annotations"]!["readOnlyHint"]!.GetValue<bool>()).ToArray();

        Assert.Equal(7, reads.Length);
        Assert.Equal(6, writes.Length);

        foreach (var tool in reads)
        {
            Assert.Equal("low", tool!["_meta"]!["sdemo/risk"]!.GetValue<string>());
            Assert.Equal("none", tool["_meta"]!["sdemo/approval"]!.GetValue<string>());
            Assert.False(tool["_meta"]!["sdemo/writes"]!.GetValue<bool>());
        }

        foreach (var tool in writes)
        {
            Assert.NotNull(tool);
            Assert.Equal("medium", tool["_meta"]!["sdemo/risk"]!.GetValue<string>());
            Assert.Equal("once", tool["_meta"]!["sdemo/approval"]!.GetValue<string>());
            Assert.True(tool["_meta"]!["sdemo/writes"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void 加议题会落一条审计()
    {
        var payload = PayloadOf(Call(ToolCall(20, "meeting_add_topic", new JsonObject
        {
            ["id"] = _meetingId,
            ["title"] = "客户 A 需求变更",
            ["refs"] = new JsonArray("sdemo.kb/doc/01H9@v7"),
        })));

        Assert.Equal("客户 A 需求变更", payload["title"]!.GetValue<string>());

        var audit = Assert.Single(_db.Session.ListAudit(_meetingId));
        Assert.Equal("meeting_add_topic", audit.Tool);
        Assert.Equal("create", audit.Action);
        Assert.Equal("topic", audit.EntityType);
        Assert.Equal("agent", audit.Actor.ToString().ToLowerInvariant());
        Assert.Equal("ok", audit.Outcome);
        Assert.Equal(payload["id"]!.GetValue<string>(), audit.EntityId);
    }

    [Fact]
    public void 确认决议幂等且每次都有审计()
    {
        var domain = new MeetingDomainService(_db.Session);
        var decision = domain.AddDecision(_meetingId, "灰度两周");

        var first = PayloadOf(Call(ToolCall(21, "meeting_confirm_decision",
            new JsonObject { ["decisionId"] = decision.Id })));
        Assert.Equal("confirmed", first["status"]!.GetValue<string>());

        var second = PayloadOf(Call(ToolCall(22, "meeting_confirm_decision",
            new JsonObject { ["decisionId"] = decision.Id })));
        Assert.Equal("confirmed", second["status"]!.GetValue<string>());

        Assert.Equal(2, _db.Session.ListAudit(_meetingId).Count(a => a.Tool == "meeting_confirm_decision"));
    }

    [Fact]
    public void 写工具失败也落审计_并带错误码()
    {
        var domain = new MeetingDomainService(_db.Session);
        var action = domain.AddAction(_meetingId, "写验收标准");

        var response = Call(ToolCall(23, "meeting_mark_action", new JsonObject
        {
            ["actionId"] = action.Id,
            ["status"] = "intaken",   // 缺 plannerRef
        }));

        Assert.Equal(-32000, response!["error"]!["code"]!.GetValue<int>());

        var audit = Assert.Single(_db.Session.ListAudit(_meetingId));
        Assert.Equal("error", audit.Outcome);
        Assert.Contains("MTG_INVALID_TRANSITION", audit.Error);
    }

    [Fact]
    public void 审计只存截断摘要_不存正文全文()
    {
        var longText = new string('长', 1200);

        Call(ToolCall(24, "meeting_add_decision", new JsonObject
        {
            ["id"] = _meetingId,
            ["text"] = longText,
        }));

        var audit = Assert.Single(_db.Session.ListAudit(_meetingId));
        Assert.NotNull(audit.ArgsSummary);
        Assert.True(audit.ArgsSummary!.Length <= MeetingAuditLog.MaxSummaryChars + 1,
            $"摘要过长：{audit.ArgsSummary.Length}");
        Assert.DoesNotContain(longText, audit.ArgsSummary);
    }

    [Fact]
    public void 写候选后能在_read_工具里看到()
    {
        Call(ToolCall(25, "meeting_add_action", new JsonObject
        {
            ["id"] = _meetingId,
            ["text"] = "补回滚演练记录",
            ["owner"] = "王五",
        }));

        var payload = PayloadOf(Call(ToolCall(26, "meeting_actions",
            new JsonObject { ["id"] = _meetingId })));

        Assert.Equal(1, payload["candidates"]!.GetValue<int>());
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
    public void meeting_topics_带_refs_与决议候选计数()
    {
        var domain = new MeetingDomainService(_db.Session);
        var topic = domain.AddTopic(_meetingId, "客户 A 需求变更", refs: new[] { "sdemo.kb/doc/01H9@v7" });
        domain.AddDecision(_meetingId, "先灰度", topic.Id);
        domain.AddAction(_meetingId, "补演练记录", topic.Id);

        var payload = PayloadOf(Call(ToolCall(10, "meeting_topics", new JsonObject { ["id"] = _meetingId })));

        var first = Assert.Single(payload["topics"]!.AsArray())!;
        Assert.Equal("客户 A 需求变更", first["title"]!.GetValue<string>());
        Assert.Equal(1, first["decisionCount"]!.GetValue<int>());
        Assert.Equal(1, first["actionCount"]!.GetValue<int>());
        Assert.Equal("sdemo.kb/doc/01H9@v7", first["refs"]![0]!.GetValue<string>());
    }

    [Fact]
    public void meeting_decisions_默认只返回_confirmed()
    {
        var domain = new MeetingDomainService(_db.Session);
        var proposed = domain.AddDecision(_meetingId, "待定决议");
        var confirmed = domain.AddDecision(_meetingId, "已确认决议");
        domain.ConfirmDecision(confirmed.Id);

        var defaultPayload = PayloadOf(Call(ToolCall(11, "meeting_decisions", new JsonObject { ["id"] = _meetingId })));
        var onlyConfirmed = Assert.Single(defaultPayload["decisions"]!.AsArray())!;
        Assert.Equal(confirmed.Id, onlyConfirmed["id"]!.GetValue<string>());
        Assert.Equal(2, defaultPayload["total"]!.GetValue<int>());

        var allPayload = PayloadOf(Call(ToolCall(12, "meeting_decisions",
            new JsonObject { ["id"] = _meetingId, ["status"] = "all" })));
        Assert.Equal(2, allPayload["returned"]!.GetValue<int>());

        // proposed 的那条确实在库里，只是默认被过滤
        Assert.Equal(DecisionStatus.Proposed, domain.Get(_meetingId).Decisions.Single(d => d.Id == proposed.Id).Status);
    }

    [Fact]
    public void meeting_actions_区分_candidate_与_intaken()
    {
        var domain = new MeetingDomainService(_db.Session);
        var pending = domain.AddAction(_meetingId, "补回滚演练记录", owner: "王五");
        var taken = domain.AddAction(_meetingId, "写验收标准");
        domain.MarkAction(taken.Id, ActionStatus.Intaken, "sdemo.planner/todo/01HB");

        var payload = PayloadOf(Call(ToolCall(13, "meeting_actions", new JsonObject { ["id"] = _meetingId })));

        Assert.Equal(2, payload["total"]!.GetValue<int>());
        Assert.Equal(1, payload["candidates"]!.GetValue<int>());

        var candidate = payload["actions"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == pending.Id)!;
        Assert.Equal("candidate", candidate["status"]!.GetValue<string>());
        Assert.Null(candidate["plannerRef"]);

        var intaken = payload["actions"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == taken.Id)!;
        Assert.Equal("intaken", intaken["status"]!.GetValue<string>());
        Assert.Equal("sdemo.planner/todo/01HB", intaken["plannerRef"]!.GetValue<string>());
    }

    [Fact]
    public void meeting_runs_标出当前_run()    {
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
