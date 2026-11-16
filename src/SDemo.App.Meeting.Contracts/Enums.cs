namespace SDemo.App.Meeting.Contracts;

public enum MeetingStatus
{
    Recording,
    Transcribing,
    Analyzing,
    Ready,
    Archived,
}

public enum MeetingKind
{
    Weekly,
    OneOnOne,
    Customer,
    Retro,
    AdHoc,
}

/// <summary>音频来源。room 场景只有麦克风一路；online 场景是环回 + 麦克风两路。</summary>
public enum AudioSource
{
    /// <summary>麦克风（线下会议室 / 说话人自己）。</summary>
    Microphone,

    /// <summary>系统环回（线上会议里对方的声音）。</summary>
    Loopback,
}

public enum TranscriptRunStatus
{
    Queued,
    Running,
    Done,
    Failed,
    Cancelled,
    Interrupted,
}

public enum SpeakerSource
{
    None,
    Manual,
    Auto,
}

/// <summary>决议状态：只有 confirmed 才进正式纪要（meeting-design §3）。</summary>
public enum DecisionStatus
{
    Proposed,
    Confirmed,
}

/// <summary>待办候选三态。**候选永远不是待办**：只有 Agent 在 Planner 建完待办后才转 intaken。</summary>
public enum ActionStatus
{
    Candidate,
    Intaken,
    Rejected,
}

/// <summary>时间戳精度：决定块 anchor 的粒度（设计见 asr-runtime §5.2）。</summary>
public enum TimestampPrecision
{
    /// <summary>没有时间戳，只能整段引用。</summary>
    None,

    /// <summary>段级（秒级），够"跳到那句话"。</summary>
    Segment,

    /// <summary>词/字级（几十毫秒），够"高亮那个词"。</summary>
    Word,
}

public enum PunctuationMode
{
    None,
    Native,
    PostProcess,
}

/// <summary>引擎跑在哪里：决定"谁能调它"，也决定要不要 loopback 校验。</summary>
public enum AsrEndpointKind
{
    /// <summary>进程内库（whisper.cpp 等），无网络。</summary>
    InProcess,

    /// <summary>本机回环 HTTP（chat-audio 形态），只允许 127.0.0.1。</summary>
    HttpLoopback,

    /// <summary>公网 HTTP 端点（云端 ASR）。必须显式开启，音频会出本机。</summary>
    HttpRemote,
}

public enum AsrWarningCode
{
    /// <summary>语言切换密集（S1）。</summary>
    LangSwitch,

    /// <summary>低置信段占比高（S2）。</summary>
    LowConfidence,

    /// <summary>疑似幻觉：静音处出整句 / 重复短语（S3）。</summary>
    Hallucination,

    /// <summary>字符集与声明语言不符（S4）。</summary>
    CharsetMismatch,

    /// <summary>空输出。</summary>
    EmptyOutput,

    /// <summary>重复短语。</summary>
    RepeatSuspect,

    /// <summary>引擎降档（内存不足时从默认档落到低配档）——不是转写质量问题，是运行说明。</summary>
    EngineFallback,
}
