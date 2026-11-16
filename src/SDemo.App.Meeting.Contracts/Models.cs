namespace SDemo.App.Meeting.Contracts;

public sealed record MeetingRecord(
    string Id,
    string Title,
    MeetingKind Kind,
    DateTimeOffset StartAt,
    DateTimeOffset? EndAt,
    MeetingStatus Status,
    string? CurrentRunId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string Uri => MeetingConstants.ToUri(Id);
}

public sealed record AudioAssetRecord(
    string Id,
    string MeetingId,
    AudioSource Source,
    string Path,
    int SampleRate,
    int Channels,
    long Bytes,
    long DurationMs,
    string? Sha256,
    DateTimeOffset CreatedAt);

public sealed record TranscriptRunRecord(
    string Id,
    string MeetingId,
    string Engine,
    string Model,
    string ParamsJson,
    TranscriptRunStatus Status,
    string? AudioAssetId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsTerminal =>
        Status is TranscriptRunStatus.Done or TranscriptRunStatus.Failed
            or TranscriptRunStatus.Cancelled or TranscriptRunStatus.Interrupted;
}

public sealed record TranscriptSegmentRecord(
    string Id,
    string RunId,
    int Ordinal,
    int StartMs,
    int EndMs,
    string Text,
    double? Confidence,
    string? Language,
    string? Speaker,
    SpeakerSource SpeakerSource,
    string? Engine,
    IReadOnlyList<AsrWarningCode> Warnings)
{
    public string FormatTimecode() =>
        $"{TimeSpan.FromMilliseconds(StartMs):hh\\:mm\\:ss}";
}

/// <summary>一次 run 的完整结果（run + 它的段）。</summary>
public sealed record TranscriptSlice(
    TranscriptRunRecord Run,
    IReadOnlyList<TranscriptSegmentRecord> Segments)
{
    public int TotalChars => Segments.Sum(s => s.Text.Length);
}
