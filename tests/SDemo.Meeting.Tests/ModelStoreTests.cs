using SDemo.App.Meeting.Asr;
using SDemo.App.Meeting.Contracts;

namespace SDemo.Meeting.Tests;

public sealed class ModelStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _srcDir;
    private readonly MeetingModelStore _store;

    public ModelStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meeting-models", Guid.NewGuid().ToString("N"));
        _srcDir = Path.Combine(Path.GetTempPath(), "meeting-model-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_srcDir);
        _store = new MeetingModelStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_srcDir, recursive: true); } catch { }
    }

    [Fact]
    public void 空目录时默认模型报未安装()
    {
        var models = _store.List();
        var turbo = models.Single(m => m.Definition.Id == ModelPacks.WhisperTurboQ8);

        Assert.Equal(ModelStatus.Missing, turbo.Status);
        Assert.Contains(ModelPacks.WhisperTurboQ8, modelIds(models));
    }

    [Fact]
    public void 导入自定义模型后可列出并回读_sha()
    {
        var source = WriteFile("my-model.bin", 4096);
        var result = _store.Import(source);

        Assert.True(File.Exists(result.Path));
        Assert.Equal(4096, result.Bytes);
        Assert.Equal(64, result.Sha256.Length);

        var listed = _store.List().Single(m => m.Definition.Id == result.Definition.Id);
        Assert.Equal(ModelStatus.Ready, listed.Status);
        Assert.Equal("imported", listed.Source);
    }

    [Fact]
    public void 导入已知包但大小不符会被拒绝()
    {
        var source = WriteFile("fake-turbo.bin", 1024);

        var ex = Assert.Throws<MeetingException>(() =>
            _store.Import(source, ModelPacks.WhisperTurboQ8));

        Assert.Contains("文件大小不符", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, ModelPacks.Default.FileName)));
    }

    [Fact]
    public void 重复导入不同内容会被拒绝()
    {
        var source = WriteFile("dup.bin", 1024);
        var imported = _store.Import(source);

        // 换一份内容、大小相同 → 同一个 packId 的 sha256 对不上
        var other = Path.Combine(_srcDir, "dup-tampered.bin");
        File.WriteAllBytes(other, Enumerable.Repeat((byte)0x42, 1024).ToArray());

        var ex = Assert.Throws<MeetingException>(() => _store.Import(other, imported.Definition.Id));
        Assert.Contains("不符", ex.Message);
    }

    [Fact]
    public void 篡改文件后校验能发现()
    {
        var source = WriteFile("tamper.bin", 2048);
        var imported = _store.Import(source);

        // 改内容但保持同样大小
        using (var fs = new FileStream(imported.Path, FileMode.Open, FileAccess.Write))
        {
            fs.WriteByte(0xFF);
        }

        var verified = _store.Verify(imported.Definition.Id);
        Assert.Equal(ModelStatus.HashMismatch, verified.Status);
    }

    [Fact]
    public void 移除后文件与记录都消失()
    {
        var source = WriteFile("remove-me.bin", 1024);
        var imported = _store.Import(source);
        Assert.True(File.Exists(imported.Path));

        Assert.True(_store.Remove(imported.Definition.Id));

        Assert.False(File.Exists(imported.Path));
        // 自定义包在移除后连"已知模型"都不再成立，所以不该出现在列表里
        Assert.DoesNotContain(_store.List(), m => m.Definition.Id == imported.Definition.Id);
    }

    [Fact]
    public void 未就绪时解析路径给出可照做的提示()
    {
        var ex = Assert.Throws<MeetingException>(() => _store.ResolveReadyPath(ModelPacks.WhisperTurboQ8));
        Assert.Contains("模型未安装", ex.Message);
        Assert.Contains("--import", ex.Message);
    }

    [Fact]
    public void manifest_损坏时不影响模型可用性()
    {
        File.WriteAllText(Path.Combine(_dir, MeetingModelStore.ManifestFileName), "{ 这不是 json");

        // 不抛异常，按"无记录"处理，文件本身仍可被列出
        var models = _store.List();
        Assert.NotEmpty(models);
    }

    private static IEnumerable<string> modelIds(IEnumerable<InstalledModel> models) =>
        models.Select(m => m.Definition.Id);

    private string WriteFile(string name, int bytes)
    {
        var path = Path.Combine(_srcDir, name);
        File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(i => (byte)(i % 251)).ToArray());
        return path;
    }
}
