namespace SDemo.App.Meeting.Contracts;

/// <summary>模型在什么档位用：默认装哪个、什么情况下才用。</summary>
public enum ModelTier
{
    /// <summary>开箱即用，安装包自带。</summary>
    Default,

    /// <summary>低配回落（内存不足 / 老机器）。</summary>
    Compact,

    /// <summary>升级档（有独显或要更强多语种时按需导入）。</summary>
    Upgrade,
}

/// <summary>
/// 一个模型包的"身份"：名字、文件名、期望大小与 sha256。
/// **权重本身不进 git**——安装包把它们打进去，或用 `meeting models --import` 由用户导入。
/// </summary>
public sealed record ModelPackDefinition(
    string Id,
    string DisplayName,
    string FileName,
    long ExpectedBytes,
    string? Sha256,
    TimestampPrecision Timestamps,
    IReadOnlyList<string> Languages,
    ModelTier Tier)
{
    public bool Optional => Tier != ModelTier.Default;

    public string ExpectedSizeText => ExpectedBytes <= 0 ? "未知" : $"{ExpectedBytes / 1024d / 1024d:F0} MB";
}

/// <summary>
/// 内置模型清单。新增模型只加一条；sha256 用于导入与启动校验，未知时可以留 null（只校验大小）。
/// </summary>
public static class ModelPacks
{
    public const string WhisperTurboQ8 = "whisper-turbo-q8";
    public const string WhisperSmallQ5 = "whisper-small-q5";
    public const string Qwen3Asr06b = "qwen3-asr-0.6b";

    /// <summary>whisper large-v3-turbo（Q8_0，874 MB，官方 ggml-org/whisper.cpp 转换）。</summary>
    public const long TurboBytes = 874_188_075;
    public const string TurboSha256 = "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1";

    public static IReadOnlyList<ModelPackDefinition> All { get; } = new[]
    {
        new ModelPackDefinition(
            WhisperTurboQ8,
            "whisper large-v3-turbo (Q8_0)",
            "ggml-large-v3-turbo-q8_0.bin",
            TurboBytes,
            TurboSha256,
            TimestampPrecision.Segment,
            new[] { "zh", "en", "ja", "yue", "auto" },
            ModelTier.Default),

        new ModelPackDefinition(
            WhisperSmallQ5,
            "whisper small (Q5_1)",
            "ggml-small-q5_1.bin",
            0, // 未实测，留空只校验"文件存在"
            null,
            TimestampPrecision.Segment,
            new[] { "zh", "en", "ja", "auto" },
            ModelTier.Compact),

        new ModelPackDefinition(
            Qwen3Asr06b,
            "Qwen3-ASR 0.6B (Q8_0, GGUF)",
            "Qwen3-ASR-0.6B-Q8_0.gguf",
            0, // 走 llama.cpp server（外部进程），不由 Meeting 直接加载
            null,
            TimestampPrecision.Segment,
            new[] { "zh", "en", "ja", "auto" },
            ModelTier.Upgrade),
    };

    public static ModelPackDefinition? Find(string idOrFileName) =>
        All.FirstOrDefault(p => string.Equals(p.Id, idOrFileName, StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(p => string.Equals(p.FileName, idOrFileName, StringComparison.OrdinalIgnoreCase));

    public static ModelPackDefinition Default => All.First(p => p.Tier == ModelTier.Default);
}
