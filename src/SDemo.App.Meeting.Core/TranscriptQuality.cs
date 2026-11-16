using System.Text.RegularExpressions;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

/// <summary>
/// 质检信号 S1–S4（asr-runtime §3.1）。这里的结论只用于"给用户一条提示"，
/// 不允许自动触发重转——重活要用户点头。
/// </summary>
public static partial class TranscriptQuality
{
    /// <param name="segments">转写结果。</param>
    /// <param name="declaredLanguage">会议声明的主语言（zh/en/ja），null 表示自动。</param>
    public static IReadOnlyList<AsrWarning> Analyze(IReadOnlyList<AsrSegment> segments, string? declaredLanguage = null)
    {
        var warnings = new List<AsrWarning>();
        if (segments.Count == 0)
        {
            warnings.Add(new AsrWarning(AsrWarningCode.EmptyOutput, 0, "整段没有识别出任何文字"));
            return warnings;
        }

        // S1 语言切换密集：同一段里出现的不同语言 ≥2 且切换次数 ≥5 次/10 分钟
        var switches = CountLanguageSwitches(segments);
        var spanMinutes = Math.Max(1, segments[^1].EndMs / 60000.0);
        if (switches >= 5 * Math.Max(1, (int)Math.Round(spanMinutes / 10.0)))
        {
            warnings.Add(new AsrWarning(AsrWarningCode.LangSwitch, segments[0].StartMs,
                $"检测到 {switches} 次语言切换，可能漏字"));
        }

        // S2 低置信段占比
        var scored = segments.Where(s => s.Confidence.HasValue).ToArray();
        if (scored.Length > 0)
        {
            var low = scored.Count(s => s.Confidence!.Value < 0.5f);
            if (low / (double)scored.Length > 0.3)
            {
                warnings.Add(new AsrWarning(AsrWarningCode.LowConfidence, segments[0].StartMs,
                    $"{low}/{scored.Length} 段置信度偏低"));
            }
        }

        // S3 幻觉：极短段里出现极长重复
        foreach (var s in segments.Where(s => LooksRepeated(s.Text)))
        {
            warnings.Add(new AsrWarning(AsrWarningCode.RepeatSuspect, s.StartMs, "疑似重复/幻觉输出"));
        }

        // S4 字符集与声明语言不符
        if (declaredLanguage is { Length: > 0 } declared)
        {
            foreach (var s in segments.Where(s => IsCharsetMismatch(s.Text, declared)))
            {
                warnings.Add(new AsrWarning(AsrWarningCode.CharsetMismatch, s.StartMs,
                    $"声明 {declared}，但这段是其他字符集"));
            }
        }

        return warnings;
    }

    public static int CountLanguageSwitches(IReadOnlyList<AsrSegment> segments)
    {
        var n = 0;
        string? previous = null;
        foreach (var seg in segments)
        {
            var lang = seg.Language;
            if (string.IsNullOrWhiteSpace(lang)) continue;
            if (previous is not null && !string.Equals(previous, lang, StringComparison.OrdinalIgnoreCase)) n++;
            previous = lang;
        }

        return n;
    }

    public static bool LooksRepeated(string text)
    {
        if (text.Length < 24) return false;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i + 6 <= text.Length; i++)
        {
            var gram = text.Substring(i, 6);
            var n = counts.GetValueOrDefault(gram) + 1;
            counts[gram] = n;
            if (n >= 4) return true;
        }

        return false;
    }

    public static bool IsCharsetMismatch(string text, string declaredLanguage)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var kana = Kana().IsMatch(text);
        var han = Han().IsMatch(text);
        var latin = Latin().Count(text) > text.Length * 0.6;

        return declaredLanguage.ToLowerInvariant() switch
        {
            "zh" => kana,
            "ja" => latin && !han,
            "en" => han || kana,
            _ => false,
        };
    }

    [GeneratedRegex(@"[\u3040-\u30ff]")]
    private static partial Regex Kana();

    [GeneratedRegex(@"[\u4e00-\u9fff]")]
    private static partial Regex Han();

    [GeneratedRegex(@"[A-Za-z]")]
    private static partial Regex Latin();
}
