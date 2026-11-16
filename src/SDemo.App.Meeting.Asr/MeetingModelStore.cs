using System.Security.Cryptography;
using System.Text.Json;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Asr;

public enum ModelStatus
{
    /// <summary>文件在、大小/校验都对。</summary>
    Ready,

    /// <summary>没装。</summary>
    Missing,

    /// <summary>文件在，但大小与清单不符。</summary>
    SizeMismatch,

    /// <summary>文件在、大小对，但 sha256 不符（被改动或下载损坏）。</summary>
    HashMismatch,
}

public sealed record InstalledModel(
    ModelPackDefinition Definition,
    string Path,
    long ActualBytes,
    string? ActualSha256,
    string Source,
    ModelStatus Status)
{
    public bool IsReady => Status == ModelStatus.Ready;
}

public sealed record ImportResult(ModelPackDefinition Definition, string Path, long Bytes, string Sha256, bool Overwrote);

/// <summary>
/// 模型库：manifest + 文件管理 + 校验。
///
/// 权重不进 git，两条来源：
///   ① 安装包自带（`tools/pack-models.ps1` 把 default 档打进去）；
///   ② 用户导入（`meeting models --import &lt;path&gt;`）。
/// 两者落到同一目录、同一份 `models.json`；自定义包也登记在里面，所以能列出、校验、移除。
/// </summary>
public sealed class MeetingModelStore
{
    public const string ManifestFileName = "models.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MeetingModelStore(string? modelDir = null) =>
        ModelDir = modelDir ?? MeetingConstants.ResolveModelDir();

    public string ModelDir { get; }

    public string ManifestPath => Path.Combine(ModelDir, ManifestFileName);

    public string PathOf(ModelPackDefinition definition) => Path.Combine(ModelDir, definition.FileName);

    // ── 查询 ────────────────────────────────────────────────────────────

    /// <summary>内置清单 + manifest 里的自定义包，合并后的全部已知模型。</summary>
    public IReadOnlyList<ModelPackDefinition> Definitions()
    {
        var manifest = LoadManifest();
        var list = ModelPacks.All.ToList();

        foreach (var entry in manifest.Packs.Values)
        {
            if (list.Any(d => string.Equals(d.Id, entry.PackId, StringComparison.OrdinalIgnoreCase))) continue;

            list.Add(new ModelPackDefinition(
                entry.PackId,
                entry.DisplayName ?? entry.FileName,
                entry.FileName,
                entry.Bytes,
                entry.Sha256,
                TimestampPrecision.Segment,
                entry.Languages ?? new[] { "auto" },
                ModelTier.Upgrade));
        }

        return list;
    }

    public IReadOnlyList<InstalledModel> List()
    {
        var manifest = LoadManifest();
        var result = new List<InstalledModel>();

        foreach (var definition in Definitions())
        {
            var entry = manifest.Packs.GetValueOrDefault(definition.Id);
            var path = PathOf(definition);
            var exists = File.Exists(path);
            var bytes = exists ? new FileInfo(path).Length : 0;

            var status = !exists
                ? ModelStatus.Missing
                : definition.ExpectedBytes > 0 && bytes != definition.ExpectedBytes
                    ? ModelStatus.SizeMismatch
                    : ModelStatus.Ready;

            result.Add(new InstalledModel(definition, path, bytes, entry?.Sha256, entry?.Source ?? "bundled", status));
        }

        return result;
    }

    public InstalledModel? Get(string packId) =>
        List().FirstOrDefault(m => string.Equals(m.Definition.Id, packId, StringComparison.OrdinalIgnoreCase));

    /// <summary>取一个"已就绪"的模型路径；没装或校验不过都抛可读错误。</summary>
    public string ResolveReadyPath(string packId)
    {
        var model = Get(packId)
            ?? throw new MeetingException(MeetingErrorCodes.AsrUnavailable, $"未知模型包：{packId}");

        return model.Status switch
        {
            ModelStatus.Ready => model.Path,
            ModelStatus.Missing => throw new MeetingException(MeetingErrorCodes.AsrUnavailable,
                $"模型未安装：{model.Definition.DisplayName}（{model.Definition.ExpectedSizeText}）。" +
                "安装包应自带默认模型；也可用 `meeting models --import <文件> --id " + model.Definition.Id + "` 导入"),
            ModelStatus.SizeMismatch => throw new MeetingException(MeetingErrorCodes.AsrUnavailable,
                $"模型大小不符：{model.Path} 实际 {model.ActualBytes / 1024d / 1024d:F0} MB，" +
                $"期望 {model.Definition.ExpectedSizeText}；请重新导入"),
            _ => throw new MeetingException(MeetingErrorCodes.AsrUnavailable,
                $"模型校验失败：{model.Path} 的 sha256 与清单不符；请重新导入"),
        };
    }

    // ── 导入 / 移除 ─────────────────────────────────────────────────────

    public ImportResult Import(string sourcePath, string? packId = null, bool overwrite = false)
    {
        if (!File.Exists(sourcePath))
            throw new MeetingException(MeetingErrorCodes.InvalidArgument, $"找不到模型文件：{sourcePath}");

        var source = new FileInfo(sourcePath);
        var definition = ResolveImportTarget(source, packId);

        if (definition.ExpectedBytes > 0 && source.Length != definition.ExpectedBytes)
        {
            throw new MeetingException(MeetingErrorCodes.InvalidArgument,
                $"文件大小不符：{source.Length / 1024d / 1024d:F0} MB，期望 {definition.ExpectedSizeText}");
        }

        var sha = ComputeSha256(sourcePath);

        if (definition.Sha256 is { Length: > 0 } expected
            && !string.Equals(expected, sha, StringComparison.OrdinalIgnoreCase))
        {
            throw new MeetingException(MeetingErrorCodes.InvalidArgument,
                $"sha256 不符：{sha[..12]}…，期望 {expected[..12]}…（文件可能下载损坏或被改动）");
        }

        Directory.CreateDirectory(ModelDir);
        var destination = PathOf(definition);
        var existedBefore = File.Exists(destination);

        if (existedBefore && !overwrite)
        {
            var sameSize = new FileInfo(destination).Length == source.Length;
            if (sameSize && string.Equals(ComputeSha256(destination), sha, StringComparison.OrdinalIgnoreCase))
                return new ImportResult(definition, destination, source.Length, sha, Overwrote: false);

            throw new MeetingException(MeetingErrorCodes.InvalidArgument,
                $"已存在同名但内容不同的模型文件：{destination}；加 --overwrite 覆盖");
        }

        // 先写 .tmp 再原子替换，避免半截文件被当成"就绪"
        var temp = destination + ".tmp";
        File.Copy(sourcePath, temp, overwrite: true);
        File.Move(temp, destination, overwrite: true);

        var manifest = LoadManifest();
        manifest.Packs[definition.Id] = new InstalledModelEntry(
            definition.Id,
            definition.FileName,
            definition.DisplayName,
            source.Length,
            sha,
            "imported",
            DateTimeOffset.UtcNow,
            definition.Tier.ToString(),
            definition.Languages.ToArray());
        SaveManifest(manifest);

        return new ImportResult(definition, destination, source.Length, sha, existedBefore);
    }

    public bool Remove(string packId)
    {
        var model = Get(packId);
        if (model is null) return false;

        var removed = false;
        if (File.Exists(model.Path))
        {
            File.Delete(model.Path);
            removed = true;
        }

        var manifest = LoadManifest();
        if (manifest.Packs.Remove(model.Definition.Id)) removed = true;

        if (removed) SaveManifest(manifest);
        return removed;
    }

    /// <summary>强制重算 sha256（导入时已算过，这里用于"体检"）。</summary>
    public InstalledModel Verify(string packId)
    {
        var model = Get(packId)
            ?? throw new MeetingException(MeetingErrorCodes.InvalidArgument, $"未知模型包：{packId}");

        if (!File.Exists(model.Path)) return model with { Status = ModelStatus.Missing };

        var definition = model.Definition;
        var manifest = LoadManifest();
        var entry = manifest.Packs.GetValueOrDefault(definition.Id);

        // 期望值优先用清单里的官方 hash；自定义包没有官方 hash，就比"导入时记录的 hash"
        var expected = definition.Sha256 is { Length: > 0 } official ? official : entry?.Sha256;
        var sha = ComputeSha256(model.Path);

        var status = expected is { Length: > 0 } && !string.Equals(expected, sha, StringComparison.OrdinalIgnoreCase)
            ? ModelStatus.HashMismatch
            : definition.ExpectedBytes > 0 && model.ActualBytes != definition.ExpectedBytes
                ? ModelStatus.SizeMismatch
                : ModelStatus.Ready;

        manifest.Packs[definition.Id] = new InstalledModelEntry(
            definition.Id, definition.FileName, definition.DisplayName, model.ActualBytes, sha,
            entry?.Source ?? "bundled", DateTimeOffset.UtcNow,
            definition.Tier.ToString(), definition.Languages.ToArray());
        SaveManifest(manifest);

        return model with { ActualSha256 = sha, Status = status };
    }

    public long TotalBytes() => List().Where(m => m.Status != ModelStatus.Missing).Sum(m => m.ActualBytes);

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    private ModelPackDefinition ResolveImportTarget(FileInfo source, string? packId)
    {
        if (packId is not null)
        {
            // 已知包既可能在 内置清单里，也可能是之前导入过的自定义包
            return Definitions().FirstOrDefault(d => string.Equals(d.Id, packId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new MeetingException(MeetingErrorCodes.InvalidArgument,
                       $"未知模型包：{packId}（可用：{string.Join(", ", Definitions().Select(d => d.Id))}）");
        }

        return ModelPacks.Find(source.Name) ?? new ModelPackDefinition(
            $"custom-{Path.GetFileNameWithoutExtension(source.Name)}",
            source.Name,
            source.Name,
            source.Length,
            null,
            TimestampPrecision.Segment,
            new[] { "auto" },
            ModelTier.Upgrade);
    }

    private ModelManifest LoadManifest()
    {
        if (!File.Exists(ManifestPath)) return new ModelManifest();

        try
        {
            var json = File.ReadAllText(ManifestPath);
            return JsonSerializer.Deserialize<ModelManifest>(json, JsonOptions) ?? new ModelManifest();
        }
        catch (JsonException)
        {
            // manifest 坏了不能让模型不可用：按"无记录"处理，文件本身还在
            return new ModelManifest();
        }
    }

    private void SaveManifest(ModelManifest manifest)
    {
        Directory.CreateDirectory(ModelDir);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    internal sealed class ModelManifest
    {
        public int Version { get; set; } = 2;

        public Dictionary<string, InstalledModelEntry> Packs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record InstalledModelEntry(
        string PackId,
        string FileName,
        string? DisplayName,
        long Bytes,
        string? Sha256,
        string Source,
        DateTimeOffset ImportedAt,
        string? Tier = null,
        string[]? Languages = null);
}
