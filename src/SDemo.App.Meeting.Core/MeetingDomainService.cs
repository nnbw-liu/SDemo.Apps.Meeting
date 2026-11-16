using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

/// <summary>
/// 议题 / 决议 / 待办候选的领域规则。三条硬约束：
/// 1. 议题挂会议，决议与候选挂议题（可空）——跨会议一律拒绝；
/// 2. 决议只有 <c>confirmed</c> 才算数，但状态提升是显式动作，不自动发生；
/// 3. **候选永远不是待办**：转 <c>intaken</c> 必须带 plannerRef，由 Agent 回写。
/// </summary>
public sealed class MeetingDomainService
{
    private const int MaxTitleChars = 200;
    private const int MaxTextChars = 2000;

    private readonly Storage.MeetingSession _session;
    private readonly TimeProvider _clock;

    public MeetingDomainService(Storage.MeetingSession session, TimeProvider? clock = null)
    {
        _session = session;
        _clock = clock ?? TimeProvider.System;
    }

    public MeetingDomain Get(string meetingId)
    {
        var meeting = _session.GetMeeting(meetingId)
                      ?? throw new MeetingException(MeetingErrorCodes.MeetingNotFound, $"会议不存在：{meetingId}");

        return new MeetingDomain(
            meeting,
            _session.ListTopics(meetingId),
            _session.ListDecisions(meetingId),
            _session.ListActions(meetingId));
    }

    // ── 议题 ────────────────────────────────────────────────────────────

    public TopicRecord AddTopic(
        string meetingId,
        string title,
        string? summary = null,
        IEnumerable<string>? refs = null,
        int? ordinal = null)
    {
        RequireMeeting(meetingId);
        var cleanTitle = Require(title, "议题标题", MaxTitleChars);

        var topic = new TopicRecord(
            MeetingIds.NewId(),
            meetingId,
            ordinal ?? _session.NextTopicOrdinal(meetingId),
            cleanTitle,
            Trim(summary, MaxTextChars),
            ValidateRefs(refs),
            _clock.GetUtcNow(),
            _clock.GetUtcNow());

        return _session.InsertTopic(topic);
    }

    public TopicRecord UpdateTopic(string topicId, string? title = null, string? summary = null, IEnumerable<string>? refs = null)
    {
        var topic = _session.GetTopic(topicId)
                    ?? throw new MeetingException(MeetingErrorCodes.TopicNotFound, $"议题不存在：{topicId}");

        var updated = topic with
        {
            Title = title is null ? topic.Title : Require(title, "议题标题", MaxTitleChars),
            Summary = summary is null ? topic.Summary : Trim(summary, MaxTextChars),
            Refs = refs is null ? topic.Refs : ValidateRefs(refs),
            UpdatedAt = _clock.GetUtcNow(),
        };

        _session.UpdateTopic(updated);
        return updated;
    }

    public void RemoveTopic(string topicId)
    {
        _ = _session.GetTopic(topicId)
            ?? throw new MeetingException(MeetingErrorCodes.TopicNotFound, $"议题不存在：{topicId}");
        _session.SoftDeleteTopic(topicId);
    }

    // ── 决议 ────────────────────────────────────────────────────────────

    public DecisionRecord AddDecision(
        string meetingId,
        string text,
        string? topicId = null,
        string? rationale = null,
        string? owner = null,
        int? anchorMs = null)
    {
        RequireMeeting(meetingId);
        RequireTopicOfMeeting(topicId, meetingId);

        var decision = new DecisionRecord(
            MeetingIds.NewId(),
            meetingId,
            topicId,
            Require(text, "决议正文", MaxTextChars),
            Trim(rationale, MaxTextChars),
            Trim(owner, 64),
            DecisionStatus.Proposed,
            anchorMs,
            _clock.GetUtcNow(),
            _clock.GetUtcNow());

        return _session.InsertDecision(decision);
    }

    /// <summary>确认决议。幂等：已经是 confirmed 时直接返回。</summary>
    public DecisionRecord ConfirmDecision(string decisionId)
    {
        var decision = _session.GetDecision(decisionId)
                       ?? throw new MeetingException(MeetingErrorCodes.DecisionNotFound, $"决议不存在：{decisionId}");

        if (decision.Status == DecisionStatus.Confirmed) return decision;

        _session.UpdateDecisionStatus(decisionId, DecisionStatus.Confirmed);
        return decision with { Status = DecisionStatus.Confirmed, UpdatedAt = _clock.GetUtcNow() };
    }

    // ── 待办候选 ────────────────────────────────────────────────────────

    public ActionRecord AddAction(
        string meetingId,
        string text,
        string? topicId = null,
        string? owner = null,
        DateTimeOffset? dueAt = null,
        int? anchorMs = null)
    {
        RequireMeeting(meetingId);
        RequireTopicOfMeeting(topicId, meetingId);

        var action = new ActionRecord(
            MeetingIds.NewId(),
            meetingId,
            topicId,
            Require(text, "候选正文", MaxTextChars),
            Trim(owner, 64),
            dueAt,
            ActionStatus.Candidate,
            null,
            anchorMs,
            _clock.GetUtcNow(),
            _clock.GetUtcNow());

        return _session.InsertAction(action);
    }

    /// <summary>
    /// 改候选状态。<c>intaken</c> 必须带 plannerRef（Agent 在 Planner 建完待办后回写），
    /// 否则说明「安排」根本没发生——拒绝，避免出现"已安排但没有待办"的孤儿。
    /// </summary>
    public ActionRecord MarkAction(string actionId, ActionStatus status, string? plannerRef = null)
    {
        var action = _session.GetAction(actionId)
                     ?? throw new MeetingException(MeetingErrorCodes.ActionNotFound, $"候选不存在：{actionId}");

        var reference = string.IsNullOrWhiteSpace(plannerRef) ? action.PlannerRef : plannerRef.Trim();

        if (status == ActionStatus.Intaken && string.IsNullOrWhiteSpace(reference))
        {
            throw new MeetingException(MeetingErrorCodes.InvalidTransition,
                "转 intaken 必须带 plannerRef（先在 Planner 建好待办再把引用回写过来）");
        }

        if (reference is { Length: > 0 } && !reference.StartsWith("sdemo.", StringComparison.OrdinalIgnoreCase))
        {
            throw new MeetingException(MeetingErrorCodes.InvalidArgument,
                $"plannerRef 必须是应用 URI（sdemo.planner/...）：{reference}");
        }

        _session.UpdateActionStatus(actionId, status, reference);
        return action with { Status = status, PlannerRef = reference, UpdatedAt = _clock.GetUtcNow() };
    }

    // ── 校验 ────────────────────────────────────────────────────────────

    private MeetingRecord RequireMeeting(string meetingId) =>
        _session.GetMeeting(meetingId)
        ?? throw new MeetingException(MeetingErrorCodes.MeetingNotFound, $"会议不存在：{meetingId}");

    private void RequireTopicOfMeeting(string? topicId, string meetingId)
    {
        if (topicId is null) return;

        var topic = _session.GetTopic(topicId)
                    ?? throw new MeetingException(MeetingErrorCodes.TopicNotFound, $"议题不存在：{topicId}");

        if (!string.Equals(topic.MeetingId, meetingId, StringComparison.Ordinal))
        {
            throw new MeetingException(MeetingErrorCodes.InvalidArgument,
                $"议题 {topicId} 不属于会议 {meetingId}（跨会议引用一律拒绝）");
        }
    }

    private static string Require(string? value, string what, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new MeetingException(MeetingErrorCodes.InvalidArgument, $"{what}不能为空");

        var text = value.Trim();
        if (text.Length > maxChars)
            throw new MeetingException(MeetingErrorCodes.InvalidArgument, $"{what}太长（上限 {maxChars} 字）");

        return text;
    }

    private static string? Trim(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    private static IReadOnlyList<string> ValidateRefs(IEnumerable<string>? refs)
    {
        if (refs is null) return Array.Empty<string>();

        var list = new List<string>();
        foreach (var raw in refs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var uri = raw.Trim();

            if (!uri.StartsWith("sdemo.", StringComparison.OrdinalIgnoreCase))
                throw new MeetingException(MeetingErrorCodes.InvalidArgument,
                    $"refs 只接受应用 URI（sdemo.<app>/...）：{uri}");

            if (!list.Contains(uri, StringComparer.OrdinalIgnoreCase)) list.Add(uri);
        }

        return list;
    }
}
