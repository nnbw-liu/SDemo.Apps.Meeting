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

    public MeetingRecord RequireMeeting(string meetingId) =>
        _session.GetMeeting(meetingId) ?? throw new MeetingException(MeetingErrorCodes.MeetingNotFound, $"会议不存在：{meetingId}");

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

    /// <summary>转写成功：落段、关 run、切 current_run、会议转 Ready。</summary>
    public TranscriptSlice CompleteRun(TranscriptRunRecord run, AsrResult result, IReadOnlyList<AsrSegment>? segments = null)
    {
        var source = segments ?? result.Segments;
        var rows = source
            .Select((s, i) => new TranscriptSegmentRecord(
                MeetingIds.NewId(),
                run.Id,
                i,
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
        _session.UpdateRunStatus(run.Id, TranscriptRunStatus.Done, finishedAt: _clock.GetUtcNow());
        _session.SetCurrentRun(run.MeetingId, run.Id);
        _session.UpdateMeetingStatus(run.MeetingId, MeetingStatus.Ready, run.Id);

        var finished = _session.GetRun(run.Id)!;
        return new TranscriptSlice(finished, _session.ListSegments(run.Id));
    }

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
