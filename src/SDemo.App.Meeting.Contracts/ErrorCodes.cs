namespace SDemo.App.Meeting.Contracts;

/// <summary>领域错误码。MCP 与 CLI 都回这一套，不许临时造词。</summary>
public static class MeetingErrorCodes
{
    public const string MeetingNotFound = "MTG_MEETING_NOT_FOUND";
    public const string RunNotFound = "MTG_RUN_NOT_FOUND";
    public const string AudioNotFound = "MTG_AUDIO_NOT_FOUND";
    public const string AudioUnreadable = "MTG_AUDIO_UNREADABLE";
    public const string AsrUnavailable = "MTG_ASR_UNAVAILABLE";
    public const string AsrFailed = "MTG_ASR_FAILED";
    public const string AsrCancelled = "MTG_ASR_CANCELLED";
    public const string RunNotTerminal = "MTG_RUN_NOT_TERMINAL";
    public const string InvalidArgument = "MTG_INVALID_ARGUMENT";
    public const string NoAudioDevice = "MTG_NO_AUDIO_DEVICE";
    public const string TopicNotFound = "MTG_TOPIC_NOT_FOUND";
    public const string DecisionNotFound = "MTG_DECISION_NOT_FOUND";
    public const string ActionNotFound = "MTG_ACTION_NOT_FOUND";
    public const string InvalidTransition = "MTG_INVALID_TRANSITION";
}

public sealed class MeetingException : Exception
{
    public MeetingException(string code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    public string Code { get; }

    public static MeetingException NotFound(string what, string id) =>
        new(MeetingErrorCodes.MeetingNotFound, $"{what} 不存在：{id}");

    public static MeetingException Invalid(string message) =>
        new(MeetingErrorCodes.InvalidArgument, message);
}
