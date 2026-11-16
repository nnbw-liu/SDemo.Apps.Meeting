namespace SDemo.App.Meeting.Contracts;

/// <summary>进程级常量：appId、数据目录、URI 前缀。改这里等于改契约，要同步 docs/。</summary>
public static class MeetingConstants
{
    public const string AppId = "sdemo.meeting";

    /// <summary>MCP / 深链用的 URI 前缀：<c>sdemo.meeting/meeting/{id}</c>。</summary>
    public const string UriPrefix = "sdemo.meeting/meeting/";

    public const string DbFileName = "meeting.db";

    /// <summary>数据根目录覆盖（测试与便携模式用），默认 <c>%APPDATA%\SDemo\Apps\sdemo.meeting</c>。</summary>
    public const string DataDirEnvVar = "SDEMO_MEETING_DATA_DIR";

    /// <summary>模型目录覆盖（CI 与开发机用），默认 &lt;数据根&gt;\models。</summary>
    public const string ModelDirEnvVar = "SDEMO_MEETING_MODEL_DIR";

    public const string RecordingsDirName = "meetings";
    public const string ModelsDirName = "models";

    /// <summary>录音音频的目标格式：16 kHz / 单声道 / 16-bit PCM（whisper 与 chat-audio 都吃这个）。</summary>
    public const int TargetSampleRate = 16000;
    public const int TargetChannels = 1;
    public const int TargetBitsPerSample = 16;

    public static string DefaultDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SDemo", "Apps", AppId);

    public static string ResolveDataDir()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirEnvVar);
        return string.IsNullOrWhiteSpace(overridden) ? DefaultDataDir : overridden;
    }

    public static string ResolveModelDir()
    {
        var overridden = Environment.GetEnvironmentVariable(ModelDirEnvVar);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(ResolveDataDir(), ModelsDirName)
            : overridden;
    }

    public static string ToUri(string meetingId) => UriPrefix + meetingId;
}

/// <summary>
/// UUIDv7：时间有序，和 KB / Planner 的 id 风格一致（同一秒内也单调）。
/// </summary>
public static class MeetingIds
{
    public static string NewId() => NewUuidV7().ToString("D");

    public static Guid NewUuidV7()
    {
        Span<byte> bytes = stackalloc byte[16];
        Random.Shared.NextBytes(bytes);

        var unixMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(unixMs >> 40);
        bytes[1] = (byte)(unixMs >> 32);
        bytes[2] = (byte)(unixMs >> 24);
        bytes[3] = (byte)(unixMs >> 16);
        bytes[4] = (byte)(unixMs >> 8);
        bytes[5] = (byte)unixMs;

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant RFC 4122

        // .NET 的 Guid 前三个字段是小端，转回网络字节序才能保持字典序 == 时间序
        SwapEndian(bytes, 0, 3);
        SwapEndian(bytes, 4, 5);
        SwapEndian(bytes, 6, 7);

        return new Guid(bytes);
    }

    private static void SwapEndian(Span<byte> bytes, int a, int b) => (bytes[a], bytes[b]) = (bytes[b], bytes[a]);

    public static DateTimeOffset TimestampOf(string id) =>
        Guid.TryParse(id, out var g) ? TimestampOf(g) : DateTimeOffset.MinValue;

    public static DateTimeOffset TimestampOf(Guid id)
    {
        var bytes = id.ToByteArray();
        SwapEndian(bytes, 0, 3);
        SwapEndian(bytes, 4, 5);
        SwapEndian(bytes, 6, 7);

        ulong ms = 0;
        for (var i = 0; i < 6; i++) ms = (ms << 8) | bytes[i];

        return DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
    }
}
