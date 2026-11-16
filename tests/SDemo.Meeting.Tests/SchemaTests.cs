using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Storage;

namespace SDemo.Meeting.Tests;

public sealed class SchemaTests : IDisposable
{
    private readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void 建库后应有五张表且版本为1()
    {
        using var conn = _db.Database.Connect();

        var tables = MeetingSchema.ListTables(conn);
        Assert.Contains("mtg_meeting", tables);
        Assert.Contains("mtg_audio_asset", tables);
        Assert.Contains("mtg_transcript_run", tables);
        Assert.Contains("mtg_segment", tables);
        Assert.Contains("mtg_meta", tables);
        Assert.Equal(1, MeetingSchema.GetUserVersion(conn));
    }

    [Fact]
    public void 重复迁移是幂等的()
    {
        using var conn = _db.Database.Connect();
        MeetingSchema.Migrate(conn);
        MeetingSchema.Migrate(conn);
        Assert.Equal(MeetingSchema.CurrentVersion, MeetingSchema.GetUserVersion(conn));
    }

    [Fact]
    public void 段表对_run_加序号的唯一约束生效()
    {
        var meeting = NewMeeting();
        var run = NewRun(meeting.Id);

        var a = NewSegment(run.Id, 0, "第一段");
        _db.Session.InsertSegments(run.Id, new[] { a });

        var duplicate = NewSegment(run.Id, 0, "序号重复");
        Assert.ThrowsAny<Exception>(() => _db.Session.InsertSegments(run.Id, new[] { duplicate }));
    }

    [Fact]
    public void 进程重启可把_running_标成_interrupted()
    {
        var meeting = NewMeeting();
        var run = NewRun(meeting.Id);
        Assert.Equal(TranscriptRunStatus.Running, _db.Session.GetRun(run.Id)!.Status);

        var n = _db.Session.MarkRunningAsInterrupted();

        Assert.Equal(1, n);
        Assert.Equal(TranscriptRunStatus.Interrupted, _db.Session.GetRun(run.Id)!.Status);
    }

    [Fact]
    public void 软删的会议不出现在列表里()
    {
        var meeting = NewMeeting();
        _db.Session.SoftDeleteMeeting(meeting.Id);

        Assert.Null(_db.Session.GetMeeting(meeting.Id));
        Assert.Empty(_db.Session.ListMeetings());
        Assert.Single(_db.Session.ListMeetings(includeDeleted: true));
    }

    private MeetingRecord NewMeeting()
    {
        var now = DateTimeOffset.UtcNow;
        return _db.Session.InsertMeeting(new MeetingRecord(
            MeetingIds.NewId(), "测试会议", MeetingKind.Weekly, now, null,
            MeetingStatus.Ready, null, now, now));
    }

    private TranscriptRunRecord NewRun(string meetingId)
    {
        var run = new TranscriptRunRecord(
            MeetingIds.NewId(), meetingId, "whisper-cpp", "turbo", "{}",
            TranscriptRunStatus.Running, null, DateTimeOffset.UtcNow, null, null, null);
        return _db.Session.InsertRun(run);
    }

    private static TranscriptSegmentRecord NewSegment(string runId, int ordinal, string text) =>
        new(MeetingIds.NewId(), runId, ordinal, 0, 1000, text, null, "zh",
            null, SpeakerSource.None, null, Array.Empty<AsrWarningCode>());
}
