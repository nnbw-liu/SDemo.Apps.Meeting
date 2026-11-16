using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

/// <summary>
/// 结果页的编辑动作：标注 / 清除说话人（v1 手工，MT-05）。
/// 只改段上的 speaker 与 speaker_source，不产生新版本——说话人是标注，不是转写内容。
/// </summary>
public sealed class TranscriptEditor
{
    private readonly Storage.MeetingSession _session;

    public TranscriptEditor(Storage.MeetingSession session) => _session = session;

    public TranscriptSegmentRecord SetSpeaker(string runId, int ordinal, string? speaker)
    {
        if (_session.GetRun(runId) is null)
            throw new MeetingException(MeetingErrorCodes.RunNotFound, $"转写 run 不存在：{runId}");

        var segments = _session.ListSegments(runId);
        var target = segments.FirstOrDefault(s => s.Ordinal == ordinal)
                     ?? throw new MeetingException(MeetingErrorCodes.InvalidArgument,
                         $"run {runId} 没有第 {ordinal} 段");

        var name = string.IsNullOrWhiteSpace(speaker) ? null : speaker.Trim();
        if (name is { Length: > 64 })
            throw new MeetingException(MeetingErrorCodes.InvalidArgument, "说话人名字太长（上限 64 字）");

        var source = name is null ? SpeakerSource.None : SpeakerSource.Manual;
        _session.UpdateSegmentSpeaker(runId, ordinal, name, source);

        return target with { Speaker = name, SpeakerSource = source };
    }

    /// <summary>"从这一段起都算某某"：批量标注后续段（会议里常用）。</summary>
    public int SetSpeakerFrom(string runId, int fromOrdinal, string? speaker)
    {
        var segments = _session.ListSegments(runId);
        if (segments.Count == 0) return 0;

        var name = string.IsNullOrWhiteSpace(speaker) ? null : speaker.Trim();
        var source = name is null ? SpeakerSource.None : SpeakerSource.Manual;

        var affected = 0;
        foreach (var segment in segments.Where(s => s.Ordinal >= fromOrdinal))
        {
            _session.UpdateSegmentSpeaker(runId, segment.Ordinal, name, source);
            affected++;
        }

        return affected;
    }
}
