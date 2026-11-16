using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Asr;

/// <summary>引擎注册表：按 id 造 provider。新增引擎只加一行，上层不动。</summary>
public static class AsrEngineCatalog
{
    public const string Whisper = WhisperCppProvider.EngineId;
    public const string ChatAudio = ChatAudioProvider.EngineId;
    public const string DeepSeekTools = DeepSeekToolsProvider.EngineId;

    public static IReadOnlyList<string> EngineIds { get; } = new[] { Whisper, ChatAudio, DeepSeekTools };

    public static IAsrProvider Create(
        string engineId,
        MeetingModelCatalog? catalog = null,
        string? chatAudioBaseUrl = null,
        string? chatAudioModel = null,
        string? whisperModelFile = null,
        string? apiKey = null,
        bool allowRemote = false)
    {
        return engineId?.ToLowerInvariant() switch
        {
            Whisper or "whisper" or "whispercpp" =>
                new WhisperCppProvider(catalog, whisperModelFile),

            ChatAudio or "chat_audio" or "chat-audio" =>
                new ChatAudioProvider(
                    chatAudioBaseUrl ?? throw MeetingException.Invalid("chat-audio 需要 --base-url"),
                    chatAudioModel ?? throw MeetingException.Invalid("chat-audio 需要 --model"),
                    ChatAudioShape.OpenAiInputAudio,
                    apiKey,
                    allowRemote),

            DeepSeekTools or "deepseek" or "deepseek-asr" =>
                new DeepSeekToolsProvider(chatAudioBaseUrl, apiKey, allowRemote),

            _ => throw MeetingException.Invalid($"未知引擎：{engineId}（可用：{string.Join(", ", EngineIds)}）"),
        };
    }

    /// <summary>从环境变量取云端端点的 Key（命令行参数优先）。</summary>
    public const string ApiKeyEnvVar = "SDEMO_ASR_API_KEY";
}
