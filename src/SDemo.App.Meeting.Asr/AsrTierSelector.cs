using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Asr;

/// <summary>档位决策结果：用哪个包、为什么、是不是降档。</summary>
public sealed record TierDecision(ModelPackDefinition Pack, string? Reason, bool IsFallback)
{
    public static TierDecision Preferred(ModelPackDefinition pack) => new(pack, null, false);
}

/// <summary>
/// 按内存挑模型档位。事前判断（避免明知装不下还去加载），
/// 事中的 OOM 兜底在 <see cref="FallbackAsrProvider"/>。
/// </summary>
public static class AsrTierSelector
{
    /// <summary>
    /// 选一个能用的档：优先用户指定的包；装不下就落到同引擎的 Compact 档；
    /// 都不行就返回首选包（让引擎给出"装不下"的明确错误，而不是静默用更差的档）。
    /// </summary>
    public static TierDecision Choose(
        MeetingModelStore store,
        string preferredPackId,
        MemorySnapshot memory,
        Func<ModelPackDefinition, bool>? isUsable = null)
    {
        var installed = store.List();
        var preferred = installed.FirstOrDefault(m => SameId(m.Definition.Id, preferredPackId))
                        ?? throw new MeetingException(MeetingErrorCodes.AsrUnavailable,
                            $"未知模型包：{preferredPackId}");

        var usable = isUsable ?? ((ModelPackDefinition _) => true);

        var required = preferred.Definition.ExpectedBytes;
        if (SystemMemory.Fits(required, memory) && usable(preferred.Definition))
            return TierDecision.Preferred(preferred.Definition);

        var compact = installed
            .Where(m => m.Definition.Tier == ModelTier.Compact)
            .Where(m => m.Status == ModelStatus.Ready)
            .Where(m => SystemMemory.Fits(m.Definition.ExpectedBytes, memory))
            .Where(m => usable(m.Definition))
            .OrderBy(m => m.Definition.ExpectedBytes)
            .FirstOrDefault();

        if (compact is not null)
        {
            var reason = required <= 0
                ? $"默认档不可用，已回落到 {compact.Definition.DisplayName}"
                : $"内存不足（可用 {memory.AvailableGb:F1} GB / 需要约 " +
                  $"{(required + SystemMemory.HeadroomBytes) / 1024d / 1024d / 1024d:F1} GB），" +
                  $"已回落到 {compact.Definition.DisplayName}（更快，精度略低）";

            return new TierDecision(compact.Definition, reason, true);
        }

        return TierDecision.Preferred(preferred.Definition);
    }

    /// <summary>异常里看不出"内存不够"时，用它判断是否该降档重试。</summary>
    public static bool LooksLikeOutOfMemory(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is OutOfMemoryException) return true;

            var message = ex.Message;
            if (message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                || message.Contains("bad_alloc", StringComparison.OrdinalIgnoreCase)
                || message.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unable to allocate", StringComparison.OrdinalIgnoreCase)
                || message.Contains("not enough memory", StringComparison.OrdinalIgnoreCase)
                || message.Contains("内存", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
