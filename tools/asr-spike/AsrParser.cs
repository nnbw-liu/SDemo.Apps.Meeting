using System.Text.RegularExpressions;

namespace AsrSpike;

public sealed record ParseResult(string Text, string? DetectedLanguage, IReadOnlyList<string> Warnings);

/// <summary>
/// Qwen3-ASR 系的输出不是纯文本：常见形态是
///   <c>language Chinese&lt;asr_text&gt;今天我们先过一下部署方案。</c>
/// 这个解析器要"宽容但严格"——宽容地剥前缀，严格地留下告警。
/// </summary>
public static class AsrParser
{
    private const string AsrTag = "<asr_text>";

    private static readonly Regex ControlToken = new(@"<\|[A-Za-z0-9_]+\|>", RegexOptions.Compiled);
    private static readonly Regex LangPrefix = new(@"^\s*language\s*[:：]?\s*([A-Za-z][A-Za-z\-]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LangPrefixZh = new(@"^\s*(?:语种|语言)\s*[:：]?\s*([A-Za-z\u4e00-\u9fa5]+)", RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"^```[^\n]*\n?|```\s*$", RegexOptions.Compiled);

    public static ParseResult Parse(string? raw)
    {
        var warnings = new List<string>();
        var s = (raw ?? "").Replace("\r\n", "\n").Trim();
        s = ControlToken.Replace(s, "").Trim();

        string? lang = null;
        string text;

        var idx = s.IndexOf(AsrTag, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var head = s[..idx];
            var m = LangPrefix.Match(head);
            if (m.Success) lang = m.Groups[1].Value.Trim();
            text = s[(idx + AsrTag.Length)..];
        }
        else
        {
            var m = LangPrefix.Match(s);
            if (m.Success)
            {
                lang = m.Groups[1].Value.Trim();
                text = s[m.Length..];
                warnings.Add("NO_ASR_TAG"); // 有语言前缀但没有 <asr_text>，说明模板可能换了
            }
            else
            {
                var m2 = LangPrefixZh.Match(s);
                if (m2.Success)
                {
                    lang = m2.Groups[1].Value.Trim();
                    text = s[m2.Length..];
                    warnings.Add("NO_ASR_TAG");
                }
                else
                {
                    text = s;
                }
            }
        }

        text = Fence.Replace(text, "").Trim();
        text = Regex.Replace(text, @"[ \t]+", " ").Trim();
        text = Regex.Replace(text, @"\n{2,}", "\n").Trim();

        if (text.Length == 0) warnings.Add("EMPTY_OUTPUT");
        else if (text.Length == 1) warnings.Add("TOO_SHORT");
        if (LooksRepeated(text)) warnings.Add("REPEAT_SUSPECT");
        if (LooksRefusal(text)) warnings.Add("REFUSAL_SUSPECT");

        return new ParseResult(text, lang, warnings);
    }

    private static bool LooksRepeated(string text)
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

    private static bool LooksRefusal(string text) =>
        text.Contains("作为 AI", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("作为AI", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("我无法", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("抱歉，我", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("As an AI", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("I'm sorry", StringComparison.OrdinalIgnoreCase);

    /// <summary>不需要模型就能跑的自测；返回进程退出码。</summary>
    public static int SelfTest()
    {
        var cases = new (string Raw, string ExpectText, string? ExpectLang, string[] ExpectWarn)[]
        {
            ("language Chinese<asr_text>今天我们先过一下部署方案。", "今天我们先过一下部署方案。", "Chinese", Array.Empty<string>()),
            ("language Japanese<asr_text>では、次にリリースの話をします。", "では、次にリリースの話をします。", "Japanese", Array.Empty<string>()),
            ("<|im_end|>language German<asr_text>Hallo, wie geht's?", "Hallo, wie geht's?", "German", Array.Empty<string>()),
            ("```text\nhello world\n```", "hello world", null, Array.Empty<string>()),
            ("   ", "", null, new[] { "EMPTY_OUTPUT" }),
            ("好的好的好的好的好的好的好的好的好的好的好的好的", "好的好的好的好的好的好的好的好的好的好的好的好的", null, new[] { "REPEAT_SUSPECT" }),
            ("抱歉，我无法处理这段音频。", "抱歉，我无法处理这段音频。", null, new[] { "REFUSAL_SUSPECT" }),
            ("明天见。", "明天见。", null, Array.Empty<string>()),
            ("language English  this  is   a test", "this is a test", "English", new[] { "NO_ASR_TAG" }),
        };

        var failed = 0;
        foreach (var (raw, expectText, expectLang, expectWarn) in cases)
        {
            var r = Parse(raw);
            var okText = r.Text == expectText;
            var okLang = string.Equals(r.DetectedLanguage, expectLang, StringComparison.OrdinalIgnoreCase);
            var okWarn = expectWarn.All(w => r.Warnings.Contains(w));

            if (okText && okLang && okWarn)
            {
                Console.WriteLine($"  [ok]   {Display(raw)}  →  {Display(r.Text)}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  [FAIL] {Display(raw)}");
                Console.WriteLine($"         期望 text={Display(expectText)} lang={expectLang ?? "-"} warn=[{string.Join(",", expectWarn)}]");
                Console.WriteLine($"         实际 text={Display(r.Text)} lang={r.DetectedLanguage ?? "-"} warn=[{string.Join(",", r.Warnings)}]");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"解析器自测通过：{cases.Length} / {cases.Length}"
            : $"解析器自测失败：{failed} 条");
        return failed == 0 ? 0 : 1;
    }

    private static string Display(string s) =>
        s.Length <= 40 ? s.Replace("\n", "\\n") : s[..40].Replace("\n", "\\n") + "…";
}
