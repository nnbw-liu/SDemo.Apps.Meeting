using SDemo.App.Meeting.Asr;
using SDemo.App.Meeting.Contracts;

namespace SDemo.Meeting.Tests;

/// <summary>
/// 真实 whisper 集成测试：只有设置了 SDEMO_WHISPER_MODEL 才跑，
/// 否则跳过——CI 上不该为了一个 874 MB 的模型把测试卡住。
/// </summary>
public sealed class WhisperIntegrationTests
{
    private static string? ModelPath => Environment.GetEnvironmentVariable("SDEMO_WHISPER_MODEL");

    private static string? SamplePath => Environment.GetEnvironmentVariable("SDEMO_WHISPER_SAMPLE");

    [SkippableFact]
    public async Task 真实模型能转写并给出段级时间戳()
    {
        Skip.If(string.IsNullOrWhiteSpace(ModelPath), "未设置 SDEMO_WHISPER_MODEL，跳过真实模型测试");
        Skip.If(!File.Exists(ModelPath), $"模型不存在：{ModelPath}");

        var wav = SamplePath;
        if (string.IsNullOrWhiteSpace(wav) || !File.Exists(wav))
        {
            wav = TestAudio.WriteSilence(Path.Combine(Path.GetTempPath(), "meeting-tests", "silence-3s.wav"), seconds: 3);
        }

        var catalog = new MeetingModelCatalog(Path.GetDirectoryName(ModelPath)!);
        using var provider = new WhisperCppProvider(catalog, Path.GetFileName(ModelPath));

        Assert.True(provider.Probe().Available);

        var result = await provider.TranscribeAsync(
            new AsrJob(wav, "zh", Threads: 4), progress: null, CancellationToken.None);

        Assert.Equal(TimestampPrecision.Segment, result.Timestamps);
        Assert.All(result.Segments, s => Assert.True(s.EndMs >= s.StartMs));
        Assert.All(result.Segments, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
    }
}
