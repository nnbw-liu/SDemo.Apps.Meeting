using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Asr;

/// <summary>模型包在磁盘上的位置与清单。默认 &lt;数据根&gt;\models，可用环境变量覆盖。</summary>
public sealed class MeetingModelCatalog
{
    public const string WhisperTurboQ8 = "ggml-large-v3-turbo-q8_0.bin";
    public const string WhisperSmallQ5 = "ggml-small-q5_1.bin";

    public MeetingModelCatalog(string? modelDir = null) =>
        ModelDir = modelDir ?? MeetingConstants.ResolveModelDir();

    public string ModelDir { get; }

    public string PathOf(string fileName) => Path.Combine(ModelDir, fileName);

    public bool Exists(string fileName) => File.Exists(PathOf(fileName));

    public long SizeOf(string fileName) => Exists(fileName) ? new FileInfo(PathOf(fileName)).Length : 0;

    /// <summary>whisper 系（Whisper.net 运行时自带二进制，只需要模型文件）。</summary>
    public IReadOnlyList<AsrModelPack> WhisperPacks() => new[]
    {
        new AsrModelPack(
            WhisperTurboQ8,
            "whisper large-v3-turbo (Q8_0)",
            SizeOf(WhisperTurboQ8),
            TimestampPrecision.Segment,
            new[] { "zh", "en", "ja", "yue", "auto" },
            Optional: false),
        new AsrModelPack(
            WhisperSmallQ5,
            "whisper small (Q5_1)",
            SizeOf(WhisperSmallQ5),
            TimestampPrecision.Segment,
            new[] { "zh", "en", "ja", "auto" },
            Optional: true),
    };
}
