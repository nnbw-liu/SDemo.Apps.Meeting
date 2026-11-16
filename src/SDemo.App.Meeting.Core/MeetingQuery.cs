using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

/// <summary>列表页一行：会议 + 当前转写的摘要。</summary>
public sealed record MeetingListItem(
    MeetingRecord Meeting,
    string? Engine,
    TranscriptRunStatus? RunStatus,
    int SegmentCount,
    int CharCount);

/// <summary>详情页数据：会议头 + 音频 + 当前转写全文。</summary>
public sealed record MeetingDetail(
    MeetingRecord Meeting,
    IReadOnlyList<AudioAssetRecord> Audio,
    IReadOnlyList<TranscriptRunRecord> Runs,
    TranscriptSlice? Current);

/// <summary>结果页的只读查询。UI 只调这里，不直接碰 SQL。</summary>
public sealed class MeetingQuery
{
    private readonly Storage.MeetingSession _session;

    public MeetingQuery(Storage.MeetingSession session) => _session = session;

    public Storage.MeetingSession Session => _session;

    public IReadOnlyList<MeetingListItem> List(int limit = 100)
    {
        var meetings = _session.ListMeetings(limit);
        var runs = meetings
            .Select(m => m.CurrentRunId is { } id ? _session.GetRun(id) : null)
            .Where(r => r is not null)
            .ToArray();

        var counts = _session.CountSegmentsByRun(runs.Select(r => r!.Id).ToArray());

        return meetings.Select(m =>
        {
            var run = m.CurrentRunId is { } id ? runs.FirstOrDefault(r => r!.Id == id) : null;
            var segments = run is null ? 0 : counts.GetValueOrDefault(run.Id);
            return new MeetingListItem(m, run?.Engine, run?.Status, segments, segments);
        }).ToArray();
    }

    public MeetingDetail? GetDetail(string meetingId)
    {
        var meeting = _session.GetMeeting(meetingId);
        if (meeting is null) return null;

        var audio = _session.ListAudio(meetingId);
        var runs = _session.ListRuns(meetingId);
        var current = meeting.CurrentRunId is { } runId ? _session.GetSlice(runId) : null;

        return new MeetingDetail(meeting, audio, runs, current);
    }

    /// <summary>所有出现过的说话人名（做输入建议用）。</summary>
    public IReadOnlyList<string> KnownSpeakers(int sampleRuns = 20)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var meeting in _session.ListMeetings(sampleRuns))
        {
            if (meeting.CurrentRunId is not { } runId) continue;
            foreach (var segment in _session.ListSegments(runId))
            {
                if (!string.IsNullOrWhiteSpace(segment.Speaker)) names.Add(segment.Speaker);
            }
        }

        return names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }
}
