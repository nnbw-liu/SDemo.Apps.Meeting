using SDemo.App.Meeting.Contracts;
using SDemo.App.Meeting.Core;

namespace SDemo.Meeting.Tests;

public sealed class ResultPageTests : IDisposable
{
    private readonly TempDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void 标注说话人写回段上并标记为手工()
    {
        var (meeting, run) = Seed();
        var editor = new TranscriptEditor(_db.Session);

        var updated = editor.SetSpeaker(run.Id, 0, "李四");

        Assert.Equal("李四", updated.Speaker);
        Assert.Equal(SpeakerSource.Manual, updated.SpeakerSource);

        var reloaded = _db.Session.ListSegments(run.Id).Single(s => s.Ordinal == 0);
        Assert.Equal("李四", reloaded.Speaker);
        Assert.Equal(SpeakerSource.Manual, reloaded.SpeakerSource);
    }

    [Fact]
    public void 清空说话人会把来源退回_none()
    {
        var (_, run) = Seed();
        var editor = new TranscriptEditor(_db.Session);
        editor.SetSpeaker(run.Id, 0, "王五");

        var cleared = editor.SetSpeaker(run.Id, 0, "  ");

        Assert.Null(cleared.Speaker);
        Assert.Equal(SpeakerSource.None, cleared.SpeakerSource);
    }

    [Fact]
    public void 从此段起批量标注()
    {
        var (_, run) = Seed();
        var editor = new TranscriptEditor(_db.Session);

        var affected = editor.SetSpeakerFrom(run.Id, 1, "张三");

        Assert.Equal(2, affected); // ordinal 1 和 2
        var segments = _db.Session.ListSegments(run.Id);
        Assert.Null(segments.Single(s => s.Ordinal == 0).Speaker);
        Assert.Equal("张三", segments.Single(s => s.Ordinal == 1).Speaker);
        Assert.Equal("张三", segments.Single(s => s.Ordinal == 2).Speaker);
    }

    [Fact]
    public void 给不存在的段标注会报错()
    {
        var (_, run) = Seed();
        var editor = new TranscriptEditor(_db.Session);

        var ex = Assert.Throws<MeetingException>(() => editor.SetSpeaker(run.Id, 99, "李四"));
        Assert.Equal(MeetingErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void 列表返回当前转写的段数()
    {
        var (meeting, _) = Seed();
        var query = new MeetingQuery(_db.Session);

        var item = Assert.Single(query.List());

        Assert.Equal(meeting.Id, item.Meeting.Id);
        Assert.Equal(3, item.SegmentCount);
    }

    [Fact]
    public void 详情带当前转写全文()
    {
        var (meeting, run) = Seed();
        var query = new MeetingQuery(_db.Session);

        var detail = query.GetDetail(meeting.Id);

        Assert.NotNull(detail);
        Assert.Equal(run.Id, detail!.Current!.Run.Id);
        Assert.Equal(3, detail.Current.Segments.Count);
        Assert.Single(detail.Runs);
    }

    [Fact]
    public void 说话人建议来自已标注过的段()
    {
        var (_, run) = Seed();
        var editor = new TranscriptEditor(_db.Session);
        editor.SetSpeaker(run.Id, 0, "李四");
        editor.SetSpeaker(run.Id, 1, "王五");

        var speakers = new MeetingQuery(_db.Session).KnownSpeakers();

        Assert.Equal(new[] { "李四", "王五" }, speakers);
    }

    private (MeetingRecord Meeting, TranscriptRunRecord Run) Seed()
    {
        var now = DateTimeOffset.UtcNow;
        var meeting = _db.Session.InsertMeeting(new MeetingRecord(
            MeetingIds.NewId(), "结果页测试", MeetingKind.Weekly, now, null,
            MeetingStatus.Ready, null, now, now));

        var run = new TranscriptRunRecord(
            MeetingIds.NewId(), meeting.Id, "whisper-cpp", "turbo", "{}",
            TranscriptRunStatus.Done, null, now, now, null, null);
        _db.Session.InsertRun(run);

        _db.Session.InsertSegments(run.Id, new[]
        {
            NewSegment(run.Id, 0, 0, 1200, "第一段"),
            NewSegment(run.Id, 1, 1200, 2600, "第二段"),
            NewSegment(run.Id, 2, 2600, 4000, "第三段"),
        });

        _db.Session.SetCurrentRun(meeting.Id, run.Id);
        return (meeting, run);
    }

    private static TranscriptSegmentRecord NewSegment(string runId, int ordinal, int start, int end, string text) =>
        new(MeetingIds.NewId(), runId, ordinal, start, end, text, 0.9, "zh",
            null, SpeakerSource.None, null, Array.Empty<AsrWarningCode>());
}
