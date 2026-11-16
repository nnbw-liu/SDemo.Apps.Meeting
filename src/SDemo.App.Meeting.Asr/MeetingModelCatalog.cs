using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Asr;

/// <summary>
/// 引擎看向模型库的窗口：把"包身份 → 文件路径 / 是否就绪"翻译成引擎能用的形状。
/// 真正的文件与 manifest 管理在 <see cref="MeetingModelStore"/>。
/// </summary>
public sealed class MeetingModelCatalog
{
    public const string WhisperTurboQ8 = ModelPacks.WhisperTurboQ8;
    public const string WhisperSmallQ5 = ModelPacks.WhisperSmallQ5;

    public MeetingModelCatalog(string? modelDir = null) => Store = new MeetingModelStore(modelDir);

    public MeetingModelStore Store { get; }

    public string ModelDir => Store.ModelDir;

    /// <summary>接受包 id 或文件名；未知名字按"直接拼在模型目录下"处理（便于测试与自定义模型）。</summary>
    public string PathOf(string idOrFileName) =>
        ModelPacks.Find(idOrFileName) is { } definition
            ? Store.PathOf(definition)
            : Path.Combine(ModelDir, idOrFileName);

    public bool Exists(string idOrFileName) => File.Exists(PathOf(idOrFileName));

    public long SizeOf(string idOrFileName)
    {
        var path = PathOf(idOrFileName);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    /// <summary>给 <c>meeting models</c> 与 doctor 用的快照。</summary>
    public IReadOnlyList<InstalledModel> ListInstalled() => Store.List();

    public IReadOnlyList<AsrModelPack> WhisperPacks() =>
        Store.List()
            .Where(m => m.Definition.FileName.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase))
            .Select(m => new AsrModelPack(
                m.Definition.Id,
                m.Definition.DisplayName,
                m.ActualBytes,
                m.Definition.Timestamps,
                m.Definition.Languages,
                m.Definition.Optional))
            .ToArray();
}
