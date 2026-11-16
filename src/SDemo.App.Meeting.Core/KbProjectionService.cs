using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

/// <summary>
/// 把会议投影进 sdemo.kb（kb-design §3.4「写入面」）。
///
/// 三条约定：
/// 1. **写入自动、定格显式**：默认 `staging` + `kind=decision`；用户归档才 `promote` 到 `durable`。
/// 2. **幂等**：`idempotencyKey` 由正文内容哈希算出来，重复投影不会在 KB 里长第二份。
/// 3. **KB 不在也不阻塞**：失败只记 `Failed` + 次数，后台可重试。
///
/// 注意：KB 契约默认"会议原始转写不进 KB"（噪音/隐私/体积）；只有用户显式归档时才连转写一起写，
/// 这是产品决定，见 docs/app-plan.md P3。
/// </summary>
public sealed class KbProjectionService
{
    public const string Kind = "decision";
    public const string StagingRetention = "staging";
    public const string DurableRetention = "durable";

    private readonly Storage.MeetingSession _session;
    private readonly MeetingDomainService _domain;
    private readonly Func<IKbTransport?> _transportFactory;
    private readonly TimeProvider _clock;

    public KbProjectionService(
        Storage.MeetingSession session,
        MeetingDomainService domain,
        Func<IKbTransport?> transportFactory,
        TimeProvider? clock = null)
    {
        _session = session;
        _domain = domain;
        _transportFactory = transportFactory;
        _clock = clock ?? TimeProvider.System;
    }

    public ProjectionRecord Get(string meetingId) =>
        _session.GetProjection(meetingId) ?? ProjectionRecord.None(meetingId, KeyOf(meetingId, includeTranscript: false));

    /// <summary>投影一场会议。返回最新状态；不抛（KB 不可用记 Failed）。</summary>
    public async Task<ProjectionRecord> ProjectAsync(
        string meetingId,
        bool includeTranscript = false,
        string retention = DurableRetention,
        CancellationToken ct = default)
    {
        var domain = _domain.Get(meetingId);
        var slice = includeTranscript ? _session.GetSlice(domain.Meeting.CurrentRunId ?? string.Empty) : null;

        var document = BuildDocument(domain, slice, includeTranscript, retention);
        var previous = _session.GetProjection(meetingId);
        var attempts = (previous?.Attempts ?? 0) + 1;

        // 先落 Pending，避免中途崩了状态不明
        _session.UpsertProjection(new ProjectionRecord(
            meetingId, ProjectionStatus.Pending, previous?.DocId, previous?.DocVersion,
            document.Blocks.Count, includeTranscript, attempts, null, document.IdempotencyKey, _clock.GetUtcNow()));

        var transport = _transportFactory();
        if (transport is null)
        {
            return Fail(domain, document, attempts, includeTranscript, previous,
                "找不到 KB。sdemo.kb 可能没安装或没启动（缺 %LOCALAPPDATA%\\SDemo\\Hub\\apps\\sdemo.kb.json 或 token）。");
        }

        var result = await transport.IngestAsync(document.ToJson().ToJsonString(), ct).ConfigureAwait(false);

        if (result.DocId is null)
        {
            return Fail(domain, document, attempts, includeTranscript, previous,
                result.Error ?? "KB 没有返回 docId");
        }

        return _session.UpsertProjection(new ProjectionRecord(
            meetingId,
            ProjectionStatus.Done,
            result.DocId,
            result.Version,
            document.Blocks.Count,
            includeTranscript,
            attempts,
            null,
            document.IdempotencyKey,
            _clock.GetUtcNow()));
    }

    /// <summary>
    /// 升格（staging → durable）。**KB 只允许 sdemo.kb 界面或 sdemo.agent 调**，应用自己调会 403
    /// （KbService.IsPrivileged）。所以 Meeting 不用它：归档直接在 ingest 时带 durable。
    /// 保留这个方法给后续 Agent 中继用。
    /// </summary>
    public async Task<ProjectionRecord> PromoteAsync(string meetingId, CancellationToken ct = default)
    {
        var current = _session.GetProjection(meetingId)
                      ?? throw new MeetingException(MeetingErrorCodes.KbUnavailable,
                          "这场会议还没投影过 KB，先归档一次再升格。");

        if (current.DocId is null)
            throw new MeetingException(MeetingErrorCodes.KbUnavailable, "投影记录里没有 docId，无法升格。");

        var transport = _transportFactory()
                        ?? throw new MeetingException(MeetingErrorCodes.KbUnavailable, "找不到 KB（未安装或未启动）。");

        var payload = new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["appId"] = MeetingConstants.AppId,
                ["entityUri"] = EntityUriOf(meetingId),
            },
            ["docId"] = current.DocId,
            ["retention"] = DurableRetention,
            ["rationale"] = "用户在会议里确认归档",
        };

        var result = await transport.PromoteAsync(payload.ToJsonString(), ct).ConfigureAwait(false);

        return _session.UpsertProjection(current with
        {
            Status = result.DocId is null && result.Error is not null ? ProjectionStatus.Failed : ProjectionStatus.Done,
            LastError = result.Error,
            UpdatedAt = _clock.GetUtcNow(),
        });
    }

    /// <summary>把会议变成 KB 的 ingest 文档。纯函数，便于测试。</summary>
    public static KbIngestDocument BuildDocument(
        MeetingDomain domain,
        TranscriptSlice? slice,
        bool includeTranscript,
        string retention = DurableRetention)
    {
        var meeting = domain.Meeting;
        var blocks = new JsonArray();

        var duration = meeting.EndAt is { } end ? end - meeting.StartAt : (TimeSpan?)null;
        var header = new StringBuilder()
            .Append("会议：")
            .Append(meeting.Title)
            .Append("（")
            .Append(meeting.StartAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

        if (duration is { } d && d > TimeSpan.Zero) header.Append("，").Append($"{(int)d.TotalMinutes} 分钟");
        if (slice is not null) header.Append("，").Append(slice.Segments.Count).Append(" 段转写");
        header.Append('）');

        blocks.Add(Add(header.ToString()));

        foreach (var topic in domain.Topics)
        {
            blocks.Add(Add($"议题 {topic.Ordinal + 1}：{topic.Title}"));
            if (topic.Summary is { Length: > 0 }) blocks.Add(Add(topic.Summary));
            foreach (var r in topic.Refs) blocks.Add(Add($"引用：{r}"));
        }

        foreach (var decision in domain.Decisions)
        {
            var tag = decision.Status == DecisionStatus.Confirmed ? "已确认" : "待确认";
            var anchor = decision.AnchorMs is { } ms ? $"·{Timecode(ms)}" : string.Empty;
            blocks.Add(Add($"决议（{tag}{anchor}）：{decision.Text}"));
            if (decision.Rationale is { Length: > 0 }) blocks.Add(Add($"理由：{decision.Rationale}"));
        }

        foreach (var action in domain.Actions)
        {
            var tag = action.Status switch
            {
                ActionStatus.Intaken => "已安排",
                ActionStatus.Rejected => "不做",
                _ => "待安排",
            };
            var owner = string.IsNullOrWhiteSpace(action.Owner) ? string.Empty : $"·{action.Owner}";
            var anchor = action.AnchorMs is { } ms ? $"·{Timecode(ms)}" : string.Empty;
            blocks.Add(Add($"待办候选（{tag}{owner}{anchor}）：{action.Text}"));
        }

        if (includeTranscript && slice is not null)
        {
            foreach (var segment in slice.Segments)
            {
                var speaker = string.IsNullOrWhiteSpace(segment.Speaker) ? string.Empty : $"{segment.Speaker}：";
                blocks.Add(Add($"[{Timecode(segment.StartMs)}] {speaker}{segment.Text}"));
            }
        }

        var key = KeyOf(meeting.Id, includeTranscript, $"{retention}|{blocks.ToJsonString()}");

        return new KbIngestDocument(
            Title: $"{meeting.StartAt.ToLocalTime():yyyy-MM-dd} · {meeting.Title}",
            Kind: Kind,
            Retention: retention,
            IdempotencyKey: key,
            Blocks: blocks,
            EntityUri: EntityUriOf(meeting.Id),
            Rationale: "会议纪要（未归档前为 staging）");
    }

    private ProjectionRecord Fail(
        MeetingDomain domain,
        KbIngestDocument document,
        int attempts,
        bool includeTranscript,
        ProjectionRecord? previous,
        string error) =>
        _session.UpsertProjection(new ProjectionRecord(
            domain.Meeting.Id,
            ProjectionStatus.Failed,
            previous?.DocId,
            previous?.DocVersion,
            document.Blocks.Count,
            includeTranscript,
            attempts,
            error,
            document.IdempotencyKey,
            _clock.GetUtcNow()));

    private static JsonObject Add(string text) => new() { ["op"] = "add", ["text"] = text };

    private static string EntityUriOf(string meetingId) => $"{MeetingConstants.AppId}/meeting/{meetingId}";

    /// <summary>内容哈希做幂等键：同样的会议内容重复投影不会在 KB 里长第二份。</summary>
    private static string KeyOf(string meetingId, bool includeTranscript, string? body = null)
    {
        var payload = $"{meetingId}|{(includeTranscript ? "with-transcript" : "summary")}|{body}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"{MeetingConstants.AppId}/{meetingId}/sha256:{hash[..32]}";
    }

    private static string Timecode(int ms) => TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss");
}
