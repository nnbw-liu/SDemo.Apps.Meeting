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

    /// <summary>
    /// whisper 引擎带降档：内存装不下默认档就事前降到 low 档；
    /// 装得下也包一层，运行期 OOM 再降（见 <see cref="FallbackAsrProvider"/>）。
    /// </summary>
    public static IAsrProvider CreateWhisperWithFallback(
        MeetingModelCatalog catalog,
        string? preferredPackId = null,
        MemorySnapshot? memory = null)
    {
        var store = catalog.Store;
        var preferred = preferredPackId ?? ModelPacks.WhisperTurboQ8;
        var snapshot = memory ?? SystemMemory.Read();

        var decision = AsrTierSelector.Choose(
            store,
            preferred,
            snapshot,
            isUsable: definition => store.Get(definition.Id)?.Status == ModelStatus.Ready);

        if (decision.IsFallback)
        {
            // 事前就已经降到低配档：直接用它，但仍然保留说明
            var compact = new WhisperCppProvider(catalog, decision.Pack.Id);
            return new FallbackAsrProvider(compact, () => new WhisperCppProvider(catalog, ModelPacks.WhisperSmallQ5),
                decision.Reason, alreadyFallenBack: true);
        }

        var primary = new WhisperCppProvider(catalog, decision.Pack.Id);
        return new FallbackAsrProvider(primary, () => new WhisperCppProvider(catalog, ModelPacks.WhisperSmallQ5));
    }
}
