using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

/// <summary>
/// 会议与转写 run 的状态机。所有写操作都从这里走，Host / MCP 不直接碰 Storage 的写方法。
/// </summary>
public sealed class MeetingService
{
    private readonly Storage.MeetingSession _session;
    private readonly TimeProvider _clock;

    public MeetingService(Storage.MeetingSession session, TimeProvider? clock = null)
    {
        _session = session;
        _clock = clock ?? TimeProvider.System;
    }

    public MeetingRecord CreateMeeting(string title, MeetingKind kind = MeetingKind.AdHoc, DateTimeOffset? startAt = null)
    {
        if (string.IsNullOrWhiteSpace(title)) throw MeetingException.Invalid("会议标题不能为空");

        var now = _clock.GetUtcNow();
        var meeting = new MeetingRecord(
            MeetingIds.NewId(),
            title.Trim(),
            kind,
            startAt ?? now,
            null,
            MeetingStatus.Ready,
            null,
            now,
            now);

        return _session.InsertMeeting(meeting);
    }

    /// <summary>软删会议（回收站语义）。录音起不来时用它清掉空条目。</summary>
    public void SoftDeleteMeeting(string meetingId) => _session.SoftDeleteMeeting(meetingId);

    public MeetingRecord RequireMeeting(string meetingId) =>        _session.GetMeeting(meetingId) ?? throw new MeetingException(MeetingErrorCodes.MeetingNotFound, $"会议不存在：{meetingId}");

    public IReadOnlyList<MeetingRecord> ListMeetings(int limit = 50) => _session.ListMeetings(limit);

    public AudioAssetRecord AttachAudio(string meetingId, AudioSource source, string path, string? sha256 = null)
    {
        _ = RequireMeeting(meetingId);
        if (!File.Exists(path)) throw new MeetingException(MeetingErrorCodes.AudioNotFound, $"音频不存在：{path}");

        var info = WavFormatReader.TryRead(path, out var fmt, out var error)
            ? fmt!
            : throw new MeetingException(MeetingErrorCodes.AudioUnreadable, $"音频无法解析：{error}");

        var asset = new AudioAssetRecord(
            MeetingIds.NewId(),
            meetingId,
            source,
            Path.GetFullPath(path),
            info.SampleRate,
            info.Channels,
            new FileInfo(path).Length,
            info.DurationMs,
            sha256,
            _clock.GetUtcNow());

        return _session.InsertAudio(asset);
    }

    /// <summary>开一次转写：建 run（Running）并把会议标成 Transcribing。</summary>
    public TranscriptRunRecord BeginRun(string meetingId, string engine, string model, string paramsJson, string? audioAssetId)
    {
        _ = RequireMeeting(meetingId);

        var run = new TranscriptRunRecord(
            MeetingIds.NewId(),
            meetingId,
            engine,
            model,
            paramsJson,
            TranscriptRunStatus.Running,
            audioAssetId,
            _clock.GetUtcNow(),
            null,
            null,
            null);

        _session.InsertRun(run);
        _session.UpdateMeetingStatus(meetingId, MeetingStatus.Transcribing);
        return run;
    }

    /// <summary>
    /// 追加一段转写结果（增量持久化）。分段转写时每完成一段就落库，
    /// 这样取消/崩溃后已转的部分不会丢——"保留已完成的部分"是硬要求。
    /// </summary>
    public IReadOnlyList<TranscriptSegmentRecord> AppendSegments(
        TranscriptRunRecord run,
        IReadOnlyList<AsrSegment> segments,
        int startOrdinal)
    {
        if (segments.Count == 0) return Array.Empty<TranscriptSegmentRecord>();

        var rows = segments
            .Select((s, i) => new TranscriptSegmentRecord(
                MeetingIds.NewId(),
                run.Id,
                startOrdinal + i,
                s.StartMs,
                s.EndMs,
                s.Text,
                s.Confidence,
                s.Language,
                null,
                SpeakerSource.None,
                null,
                Array.Empty<AsrWarningCode>()))
            .ToArray();

        _session.InsertSegments(run.Id, rows);
        return rows;
    }

    /// <summary>转写成功：收尾 run、切 current_run、会议转 Ready。</summary>
    public TranscriptSlice CompleteRun(TranscriptRunRecord run, AsrResult result, IReadOnlyList<AsrSegment>? segments = null)
    {
        var source = segments ?? result.Segments;
        var existing = _session.ListSegments(run.Id).Count;

        if (existing == 0 && source.Count > 0)
            AppendSegments(run, source, 0);

        _session.UpdateRunStatus(run.Id, TranscriptRunStatus.Done, finishedAt: _clock.GetUtcNow());
        _session.SetCurrentRun(run.MeetingId, run.Id);
        _session.UpdateMeetingStatus(run.MeetingId, MeetingStatus.Ready, run.Id);

        var finished = _session.GetRun(run.Id)!;
        return new TranscriptSlice(finished, _session.ListSegments(run.Id));
    }

    /// <summary>质检结果回写到段上（S1–S4 的产物）。</summary>
    public void ApplyWarnings(string runId, IReadOnlyList<AsrWarning> warnings)
    {
        if (warnings.Count == 0) return;

        var segments = _session.ListSegments(runId);
        if (segments.Count == 0) return;

        var byOrdinal = new Dictionary<int, List<AsrWarningCode>>();
        foreach (var warning in warnings)
        {
            var target = segments.FirstOrDefault(s => warning.AtMs >= s.StartMs && warning.AtMs <= s.EndMs)
                         ?? segments[0];

            if (!byOrdinal.TryGetValue(target.Ordinal, out var list))
                byOrdinal[target.Ordinal] = list = new List<AsrWarningCode>();

            if (!list.Contains(warning.Code)) list.Add(warning.Code);
        }

        _session.UpdateSegmentWarnings(runId,
            byOrdinal.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<AsrWarningCode>)kv.Value));
    }

    /// <summary>续转：已完成多少段。</summary>
    public int CountSegments(string runId) => _session.ListSegments(runId).Count;

    /// <summary>把 run 重新标记为 Running（续转用）。</summary>
    public void MarkRunning(TranscriptRunRecord run)
    {
        _session.UpdateRunStatus(run.Id, TranscriptRunStatus.Running);
        _session.UpdateMeetingStatus(run.MeetingId, MeetingStatus.Transcribing);
    }

    public TranscriptRunRecord RequireRun(string runId) =>
        _session.GetRun(runId) ?? throw new MeetingException(MeetingErrorCodes.RunNotFound, $"转写 run 不存在：{runId}");

    public void FailRun(TranscriptRunRecord run, string code, string message)
    {
        _session.UpdateRunStatus(run.Id, TranscriptRunStatus.Failed, code, message, _clock.GetUtcNow());
        _session.UpdateMeetingStatus(run.MeetingId, MeetingStatus.Ready);
    }

    public void CancelRun(TranscriptRunRecord run)
    {
        _session.UpdateRunStatus(run.Id, TranscriptRunStatus.Cancelled, MeetingErrorCodes.AsrCancelled, "用户取消", _clock.GetUtcNow());
        _session.UpdateMeetingStatus(run.MeetingId, MeetingStatus.Ready);
    }

    /// <summary>进程启动时调用：清掉上次崩溃留下的 Running。</summary>
    public int RecoverInterruptedRuns() => _session.MarkRunningAsInterrupted();

    public TranscriptSlice? GetCurrentTranscript(string meetingId)
    {
        var meeting = _session.GetMeeting(meetingId);
        return meeting?.CurrentRunId is { } runId ? _session.GetSlice(runId) : null;
    }

    public TranscriptSlice RequireSlice(string runId) =>
        _session.GetSlice(runId) ?? throw new MeetingException(MeetingErrorCodes.RunNotFound, $"转写 run 不存在：{runId}");

    public IReadOnlyList<TranscriptRunRecord> ListRuns(string meetingId) => _session.ListRuns(meetingId);
}
