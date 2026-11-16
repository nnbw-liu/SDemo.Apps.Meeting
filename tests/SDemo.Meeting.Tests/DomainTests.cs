using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;

namespace SDemo.Meeting.Tests;

public sealed class DomainTests : IDisposable
{
    private readonly TempDb _db = new();
    private readonly MeetingDomainService _domain;
    private readonly MeetingRecord _meeting;

    public DomainTests()
    {
        _domain = new MeetingDomainService(_db.Session);
        var now = DateTimeOffset.UtcNow;
        _meeting = _db.Session.InsertMeeting(new MeetingRecord(
            MeetingIds.NewId(), "议题测试", MeetingKind.Weekly, now, null,
            MeetingStatus.Ready, null, now, now));
    }

    public void Dispose() => _db.Dispose();

    // ── 议题 ────────────────────────────────────────────────────────────

    [Fact]
    public void 议题按加入顺序编号()
    {
        var first = _domain.AddTopic(_meeting.Id, "客户 A 需求变更");
        var second = _domain.AddTopic(_meeting.Id, "内部工具重构");

        Assert.Equal(0, first.Ordinal);
        Assert.Equal(1, second.Ordinal);
        Assert.Equal(new[] { "客户 A 需求变更", "内部工具重构" },
            _domain.Get(_meeting.Id).Topics.Select(t => t.Title));
    }

    [Fact]
    public void 议题的_refs_只接受应用_URI()
    {
        var ex = Assert.Throws<MeetingException>(() =>
            _domain.AddTopic(_meeting.Id, "带坏引用", refs: new[] { "C:\\某文件.docx" }));

        Assert.Equal(MeetingErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("sdemo.", ex.Message);
    }

    [Fact]
    public void 议题_refs_会去重()
    {
        var topic = _domain.AddTopic(_meeting.Id, "带引用", refs: new[]
        {
            "sdemo.kb/doc/01H9…@v7",
            "sdemo.kb/doc/01H9…@v7",
        });

        Assert.Single(topic.Refs);
    }

    [Fact]
    public void 删议题是软删_列表里不再出现()
    {
        var topic = _domain.AddTopic(_meeting.Id, "临时议题");
        _domain.RemoveTopic(topic.Id);

        Assert.Empty(_domain.Get(_meeting.Id).Topics);
        Assert.Null(_db.Session.GetTopic(topic.Id));
    }

    // ── 决议 ────────────────────────────────────────────────────────────

    [Fact]
    public void 新决议默认是_proposed_确认后才算数()
    {
        var topic = _domain.AddTopic(_meeting.Id, "部署方案");
        var decision = _domain.AddDecision(_meeting.Id, "灰度两周后切全量", topic.Id, rationale: "回滚耗时未验证");

        Assert.Equal(DecisionStatus.Proposed, decision.Status);

        var confirmed = _domain.ConfirmDecision(decision.Id);
        Assert.Equal(DecisionStatus.Confirmed, confirmed.Status);
        Assert.Equal(1, _domain.Get(_meeting.Id).ConfirmedDecisions);
    }

    [Fact]
    public void 重复确认是幂等的()
    {
        var decision = _domain.AddDecision(_meeting.Id, "定稿");
        _domain.ConfirmDecision(decision.Id);

        var again = _domain.ConfirmDecision(decision.Id);

        Assert.Equal(DecisionStatus.Confirmed, again.Status);
    }

    [Fact]
    public void 决议不能挂别的会议的议题()
    {
        var other = _db.Session.InsertMeeting(new MeetingRecord(
            MeetingIds.NewId(), "另一场", MeetingKind.AdHoc, DateTimeOffset.UtcNow, null,
            MeetingStatus.Ready, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var foreignTopic = _domain.AddTopic(other.Id, "别人的议题");

        var ex = Assert.Throws<MeetingException>(() =>
            _domain.AddDecision(_meeting.Id, "串场决议", foreignTopic.Id));

        Assert.Equal(MeetingErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("不属于", ex.Message);
    }

    // ── 候选 ────────────────────────────────────────────────────────────

    [Fact]
    public void 新候选是_candidate_不是待办()
    {
        var action = _domain.AddAction(_meeting.Id, "补回滚演练记录", owner: "王五");

        Assert.Equal(ActionStatus.Candidate, action.Status);
        Assert.Null(action.PlannerRef);
        Assert.True(action.IsOpen);
    }

    [Fact]
    public void 转_intaken_必须带_plannerRef()
    {
        var action = _domain.AddAction(_meeting.Id, "写验收标准");

        var ex = Assert.Throws<MeetingException>(() =>
            _domain.MarkAction(action.Id, ActionStatus.Intaken));

        Assert.Equal(MeetingErrorCodes.InvalidTransition, ex.Code);
        Assert.Contains("plannerRef", ex.Message);
    }

    [Fact]
    public void 带_plannerRef_才能转_intaken()
    {
        var action = _domain.AddAction(_meeting.Id, "写验收标准");

        var updated = _domain.MarkAction(action.Id, ActionStatus.Intaken, "sdemo.planner/todo/01HB…");

        Assert.Equal(ActionStatus.Intaken, updated.Status);
        Assert.StartsWith("sdemo.planner/", updated.PlannerRef);
        Assert.False(updated.IsOpen);
    }

    [Fact]
    public void 拒绝候选不需要_plannerRef()
    {
        var action = _domain.AddAction(_meeting.Id, "说了但没定的事");

        var updated = _domain.MarkAction(action.Id, ActionStatus.Rejected);

        Assert.Equal(ActionStatus.Rejected, updated.Status);
    }

    [Fact]
    public void 空标题与超长正文会被拒绝()
    {
        Assert.Throws<MeetingException>(() => _domain.AddTopic(_meeting.Id, "  "));
        Assert.Throws<MeetingException>(() =>
            _domain.AddDecision(_meeting.Id, new string('x', 2001)));
    }

    [Fact]
    public void 领域快照带计数()
    {
        var topic = _domain.AddTopic(_meeting.Id, "议题一");
        _domain.AddDecision(_meeting.Id, "决议一", topic.Id);
        var confirmed = _domain.AddDecision(_meeting.Id, "决议二", topic.Id);
        _domain.ConfirmDecision(confirmed.Id);
        _domain.AddAction(_meeting.Id, "候选一", topic.Id);
        _domain.AddAction(_meeting.Id, "候选二", topic.Id);

        var domain = _domain.Get(_meeting.Id);

        Assert.Single(domain.Topics);
        Assert.Equal(2, domain.Decisions.Count);
        Assert.Equal(1, domain.ConfirmedDecisions);
        Assert.Equal(2, domain.CandidateActions);
    }
}
