using System.Text.Json;
using SDemo.App.Meeting.Asr;
using SDemo.App.Meeting.Contracts;

namespace SDemo.Meeting.Tests;

public sealed class AsrContractTests
{
    [Theory]
    [InlineData(TimestampPrecision.None, AnchorGranularity.Document)]
    [InlineData(TimestampPrecision.Segment, AnchorGranularity.Sentence)]
    [InlineData(TimestampPrecision.Word, AnchorGranularity.Block)]
    public void 能力决定_anchor_粒度(TimestampPrecision precision, AnchorGranularity expected)
    {
        var caps = new AsrCapabilities(precision, PunctuationMode.Native, new[] { "zh" },
            false, false, false, AsrEndpointKind.InProcess);

        Assert.Equal(expected, caps.Anchor);
    }

    [Fact]
    public void 声明语言不在能力清单里时应被识别()
    {
        var caps = new AsrCapabilities(TimestampPrecision.Segment, PunctuationMode.Native, new[] { "zh", "en" },
            false, false, false, AsrEndpointKind.InProcess);

        Assert.True(caps.SupportsLanguage("zh"));
        Assert.True(caps.SupportsLanguage("EN"));
        Assert.True(caps.SupportsLanguage(null));
        Assert.False(caps.SupportsLanguage("ja"));
    }

    [Fact]
    public void chat_audio_只接受回环地址()
    {
        var ex = Assert.Throws<MeetingException>(() =>
            new ChatAudioProvider("http://10.0.0.5:8080", "qwen3-asr"));

        Assert.Equal(MeetingErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("--allow-remote", ex.Message);
    }

    [Fact]
    public void 云端端点显式放行但缺_key_仍然拒绝()
    {
        var ex = Assert.Throws<MeetingException>(() =>
            new ChatAudioProvider("https://api.example.com/v1", "qwen3-asr", allowRemote: true));

        Assert.Equal(MeetingErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("API Key", ex.Message);
    }

    [Fact]
    public void 云端端点带_key_可以构造且标记为非本地()
    {
        using var provider = new ChatAudioProvider(
            "https://api.example.com/v1", "qwen3-asr", apiKey: "sk-test", allowRemote: true);

        Assert.False(provider.IsLocal);
        Assert.Equal("https://api.example.com/v1/chat/completions", provider.Endpoint);
    }

    [Fact]
    public void 本地回环不需要_key()
    {
        using var provider = new ChatAudioProvider("http://127.0.0.1:8080/v1", "qwen3-asr");
        Assert.True(provider.IsLocal);
    }

    [Fact]
    public void chat_audio_端点拼接不会出现_v1_v1()
    {
        using var provider = new ChatAudioProvider("http://127.0.0.1:8080/v1", "qwen3-asr");
        Assert.Equal("http://127.0.0.1:8080/v1/chat/completions", provider.Endpoint);

        using var plain = new ChatAudioProvider("http://localhost:8080", "qwen3-asr");
        Assert.Equal("http://localhost:8080/v1/chat/completions", plain.Endpoint);
    }

    [Fact]
    public void chat_audio_载荷是_input_audio_内容块()
    {
        var payload = ChatAudioProvider.BuildPayload("qwen3-asr", ChatAudioShape.OpenAiInputAudio, new byte[] { 1, 2, 3 });
        using var doc = JsonDocument.Parse(payload);

        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("input_audio", content[1].GetProperty("type").GetString());
        Assert.Equal("wav", content[1].GetProperty("input_audio").GetProperty("format").GetString());
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            content[1].GetProperty("input_audio").GetProperty("data").GetString());
    }

    [Theory]
    [InlineData("language Chinese<asr_text>今天我们先过一下部署方案。", "今天我们先过一下部署方案。", "Chinese")]
    [InlineData("language Japanese<asr_text>では、次にリリースの話をします。", "では、次にリリースの話をします。", "Japanese")]
    [InlineData("<|im_end|>language German<asr_text>Hallo", "Hallo", "German")]
    [InlineData("```text\nhello world\n```", "hello world", null)]
    [InlineData("明天见。", "明天见。", null)]
    public void 解析器剥前缀与围栏(string raw, string expectedText, string? expectedLang)
    {
        var text = ChatAudioProvider.ExtractText(raw, out var lang);
        Assert.Equal(expectedText, text);
        Assert.Equal(expectedLang, lang);
    }

    [Fact]
    public void 解析器对_Ollama_返回结构也能取到文本()
    {
        const string ollamaJson = """
            {"message":{"role":"assistant","content":"language Chinese<asr_text>你好，世界。"}}
            """;

        var parsed = ChatAudioProvider.ParseResponse(ollamaJson);
        Assert.Equal("你好，世界。", parsed.Text);
        Assert.Equal("Chinese", parsed.Language);
    }

    [Fact]
    public void 空文本会被解析成空串而不是空引用()
    {
        var parsed = ChatAudioProvider.ParseResponse("""{"choices":[{"message":{"content":"language None<asr_text>"}}]}""");
        Assert.Equal(string.Empty, parsed.Text);
    }

    [Fact]
    public void deepseek_tools_没有_key_直接拒绝()
    {
        var ex = Assert.Throws<MeetingException>(() => new DeepSeekToolsProvider(apiKey: null));
        Assert.Equal(MeetingErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("API Key", ex.Message);
    }

    [Fact]
    public void deepseek_tools_远端必须显式放行()
    {
        var ex = Assert.Throws<MeetingException>(() =>
            new DeepSeekToolsProvider(apiKey: "sk-test", allowRemote: false));

        Assert.Equal(MeetingErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("--allow-remote", ex.Message);
    }

    [Fact]
    public void deepseek_tools_解析官方响应()
    {
        const string json = """
            {"request_id":"0b8c1c7e","text":"今天上海的天气怎么样？","duration_ms":3200}
            """;

        var parsed = DeepSeekToolsProvider.ParseResponse(json);

        Assert.Equal("今天上海的天气怎么样？", parsed.Text);
        Assert.Equal(3200, parsed.DurationMs);
        Assert.Equal("0b8c1c7e", parsed.RequestId);
    }

    [Fact]
    public async Task deepseek_tools_超过120秒直接拒绝且不发请求()
    {
        using var provider = new DeepSeekToolsProvider(apiKey: "sk-test", allowRemote: true);

        var longWav = TestAudio.WriteSilence(
            Path.Combine(Path.GetTempPath(), "meeting-tests", $"long-{Guid.NewGuid():N}.wav"), seconds: 121);

        var ex = await Assert.ThrowsAsync<MeetingException>(() =>
            provider.TranscribeAsync(new AsrJob(longWav), progress: null, CancellationToken.None));

        Assert.Equal(MeetingErrorCodes.AudioUnreadable, ex.Code);
        Assert.Contains("120", ex.Message);
    }

    [Fact]
    public void whisper_引擎在模型缺失时明确报不可用()
    {
        var catalog = new MeetingModelCatalog(Path.Combine(Path.GetTempPath(), "no-such-model-dir"));
        using var provider = new WhisperCppProvider(catalog);

        var availability = provider.Probe();

        Assert.False(availability.Available);
        Assert.Equal("模型未下载", availability.Reason);
        Assert.Contains("874", availability.Detail);
    }

    [Fact]
    public void 未知引擎抛出可读错误()
    {
        var ex = Assert.Throws<MeetingException>(() => AsrEngineCatalog.Create("nope"));
        Assert.Equal(MeetingErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("whisper-cpp", ex.Message);
    }
}
