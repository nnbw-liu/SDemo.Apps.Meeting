using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AsrSpike;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 某些终端不支持 */ }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Help();
            return 0;
        }

        var cmd = args[0].ToLowerInvariant();
        var o = Options.Parse(args, 1);

        try
        {
            return cmd switch
            {
                "probe" => await ProbeAsync(o),
                "run" => await RunAsync(o),
                "bench" => await BenchAsync(o),
                "parse" => ParseCmd(o),
                _ => Unknown(cmd),
            };
        }
        catch (ChatAudioException ex)
        {
            Err(ex.Message);
            HintForHttp(ex);
            return 2;
        }
        catch (HttpRequestException ex)
        {
            Err($"连不上服务：{ex.Message}");
            Console.Error.WriteLine("检查：");
            Console.Error.WriteLine("  · llama.cpp：llama-server 是否在跑（默认 8080）");
            Console.Error.WriteLine("  · Ollama：ollama serve 是否在跑（默认 11434）");
            Console.Error.WriteLine("  · --base-url 与 --shape 是否匹配（ollama ↔ 11434，llama ↔ 8080）");
            return 2;
        }
        catch (TaskCanceledException)
        {
            Err("请求超时。首次调用可能正在加载模型，加大超时或先手动跑一次预热。");
            return 2;
        }
        catch (Exception ex)
        {
            Err(ex.Message);
            return 1;
        }
    }

    // ── probe ───────────────────────────────────────────────────────────

    private static async Task<int> ProbeAsync(Options o)
    {
        var shape = Shape(o);
        var baseUrl = o.Str("base-url", DefaultBase(shape))!;

        using var client = new ChatAudioClient(TimeSpan.FromSeconds(15));
        Say($"探测 {baseUrl}（{shape}）…");
        var models = await client.ProbeAsync(baseUrl, shape, CancellationToken.None);

        Say("可用模型：");
        Console.WriteLine(string.IsNullOrWhiteSpace(models) ? "  （空）" : models);
        return 0;
    }

    // ── run ─────────────────────────────────────────────────────────────

    private static async Task<int> RunAsync(Options o)
    {
        var input = Require(o, "input");
        var shape = Shape(o);
        var baseUrl = o.Str("base-url", DefaultBase(shape))!;
        var model = Require(o, "model");
        var prompt = o.Str("prompt");

        var start = AudioPrep.ParseTime(o.Str("start"));
        var duration = AudioPrep.ParseTime(o.Str("duration"));

        var prepared = AudioPrep.Prepare(input, start, duration, o.Str("ffmpeg"), o.Str("out-wav"), Say);
        if (duration is null && prepared.Info.Seconds > 90)
            Warn($"整段 {prepared.Info.Seconds:F0}s 超过 90 秒；chat-audio 形态单次请求通常只吃 60 秒左右，建议加 --duration 60");

        var wav = File.ReadAllBytes(prepared.Path);

        if (o.Flag("dry-run"))
        {
            var payload = ChatAudioClient.BuildPayload(model, shape, wav, prompt);
            Say("");
            Say("dry-run（不发送）");
            Say($"  endpoint : {ChatAudioClient.EndpointOf(baseUrl, shape)}");
            Say($"  model    : {model}");
            Say($"  音频     : {prepared.Info}");
            Say($"  WAV      : {wav.Length:N0} B");
            Say($"  payload  : {Encoding.UTF8.GetByteCount(payload):N0} B（含 base64）");
            Cleanup(prepared);
            return 0;
        }

        using var client = new ChatAudioClient();
        Say($"POST {ChatAudioClient.EndpointOf(baseUrl, shape)}");

        var sw = Stopwatch.StartNew();
        var res = await client.TranscribeAsync(baseUrl, shape, model, wav, prompt, CancellationToken.None);
        sw.Stop();

        var parsed = AsrParser.Parse(res.Content);
        var rtf = res.Elapsed.TotalSeconds / Math.Max(0.001, prepared.Info.Seconds);

        Say("");
        Say("=== 结果 ===");
        Say($"  音频     : {prepared.Info}");
        Say($"  请求耗时 : {res.Elapsed.TotalSeconds:F2} s（RTF {rtf:F2}×）");
        Say($"  端到端   : {sw.Elapsed.TotalSeconds:F2} s");
        Say($"  payload  : {res.PayloadBytes:N0} B");
        Say($"  语种     : {parsed.DetectedLanguage ?? "（未识别）"}");
        Say($"  文本     : {parsed.Text}");
        Say($"  字符数   : {parsed.Text.Length}");
        Say($"  告警     : {(parsed.Warnings.Count == 0 ? "无" : string.Join(", ", parsed.Warnings))}");

        Say("");
        Say("=== 原始返回 ===");
        var raw = o.Flag("raw") || res.RawJson.Length <= 2000 ? res.RawJson : res.RawJson[..2000] + "…（--raw 看全部）";
        Console.WriteLine(raw);

        if (o.Str("save-raw") is { } savePath)
        {
            File.WriteAllText(savePath, res.RawJson, Encoding.UTF8);
            Say($"原始 JSON 已写入 {savePath}");
        }

        if (o.Str("out") is { } outFile) AppendMarkdownRow(outFile, model, shape, prepared.Info.Seconds, res.Elapsed, rtf, parsed);

        Cleanup(prepared);
        return parsed.Warnings.Contains("EMPTY_OUTPUT") ? 3 : 0;
    }

    // ── bench ───────────────────────────────────────────────────────────

    private static async Task<int> BenchAsync(Options o)
    {
        var input = Require(o, "input");
        var shape = Shape(o);
        var baseUrl = o.Str("base-url", DefaultBase(shape))!;
        var model = Require(o, "model");
        var repeat = Math.Max(1, o.Int("repeat", 3));
        var warmup = Math.Max(0, o.Int("warmup", 1));

        var start = AudioPrep.ParseTime(o.Str("start"));
        var duration = AudioPrep.ParseTime(o.Str("duration"));
        var prepared = AudioPrep.Prepare(input, start, duration, o.Str("ffmpeg"), o.Str("out-wav"), Say);
        var wav = File.ReadAllBytes(prepared.Path);

        using var client = new ChatAudioClient(TimeSpan.FromMinutes(20));

        Say($"模型 {model} · 形态 {shape} · 音频 {prepared.Info}");
        Say($"预热 {warmup} 次，测量 {repeat} 次（首次通常含模型加载，必须预热）");

        for (var i = 0; i < warmup; i++)
        {
            Say($"  预热 {i + 1}/{warmup} …");
            var r = await client.TranscribeAsync(baseUrl, shape, model, wav, o.Str("prompt"), CancellationToken.None);
            Say($"    {r.Elapsed.TotalSeconds:F2} s");
        }

        var results = new List<(TimeSpan Elapsed, int Chars, double Rtf, string Warnings)>();
        for (var i = 0; i < repeat; i++)
        {
            var r = await client.TranscribeAsync(baseUrl, shape, model, wav, o.Str("prompt"), CancellationToken.None);
            var p = AsrParser.Parse(r.Content);
            var rtf = r.Elapsed.TotalSeconds / Math.Max(0.001, prepared.Info.Seconds);
            results.Add((r.Elapsed, p.Text.Length, rtf, string.Join(",", p.Warnings)));
            Say($"  第 {i + 1} 次：{r.Elapsed.TotalSeconds:F2} s · RTF {rtf:F2}× · {p.Text.Length} 字");
        }

        var sorted = results.Select(x => x.Elapsed.TotalSeconds).OrderBy(x => x).ToArray();
        var median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;

        Say("");
        Say("=== 汇总 ===");
        Say($"  中位耗时 : {median:F2} s");
        Say($"  最小/最大: {sorted.First():F2} / {sorted.Last():F2} s");
        Say($"  中位 RTF : {median / Math.Max(0.001, prepared.Info.Seconds):F2}×（< 1 表示比实时快）");
        Say($"  推算 30 分钟音频：{median / Math.Max(0.001, prepared.Info.Seconds) * 30:F1} 分钟");

        if (o.Str("out") is { } outFile)
        {
            AppendMarkdownRow(outFile, model, shape, prepared.Info.Seconds, TimeSpan.FromSeconds(median),
                median / Math.Max(0.001, prepared.Info.Seconds),
                new ParseResult($"{results.Average(x => x.Chars):F0} 字（均值）", null, Array.Empty<string>()));
            Say($"已追加一行到 {outFile}");
        }

        Cleanup(prepared);
        return 0;
    }

    // ── parse ───────────────────────────────────────────────────────────

    private static int ParseCmd(Options o)
    {
        if (o.Flag("self-test") || o.Str("text") is null && !o.Flag("stdin"))
        {
            return AsrParser.SelfTest();
        }

        string raw;
        if (o.Str("text") is { } t) raw = t;
        else if (o.Str("file") is { } f) raw = File.ReadAllText(f, Encoding.UTF8);
        else raw = Console.In.ReadToEnd();

        var r = AsrParser.Parse(raw);
        Console.WriteLine($"text : {r.Text}");
        Console.WriteLine($"lang : {r.DetectedLanguage ?? "（未识别）"}");
        Console.WriteLine($"warn : {(r.Warnings.Count == 0 ? "无" : string.Join(", ", r.Warnings))}");
        return 0;
    }

    // ── 帮助与工具 ──────────────────────────────────────────────────────

    private static void Help()
    {
        Console.WriteLine("""
            asr-spike —— Qwen3-ASR 的 chat-audio 接入验证工具（Phase C / C1–C4）

            用法：
              asr-spike probe   --base-url http://127.0.0.1:8080 [--shape llama]
              asr-spike run     --input <音频> --model <模型> [选项]
              asr-spike bench   --input <音频> --model <模型> [--repeat 3] [--warmup 1]
              asr-spike parse   --self-test | --text "<原始输出>" | --file <txt>

            公共选项：
              --shape     llama | openai | ollama     默认按 --base-url 猜（见下）
              --base-url  默认 ollama → http://127.0.0.1:11434 ；llama → http://127.0.0.1:8080
              --prompt    发给模型的文本，默认空（Ollama 的 qwen3-asr 包要求空 prompt）
              --ffmpeg    ffmpeg 可执行文件路径（PATH 里没有时用）
              --out-wav   转码后的 WAV 落盘位置（默认临时文件，跑完删除）

            run / bench 选项：
              --start     00:12:31 或 751
              --duration  60 或 01:00       建议 ≤ 60 秒（chat-audio 单次上限）
              --dry-run   只打印请求形状与体积，不发请求
              --raw       打印完整原始返回
              --save-raw  原始 JSON 落盘
              --out       追加一行 Markdown 到基准表（直接喂 asr-runtime.md §7）
              --repeat    测量次数（默认 3）
              --warmup    预热次数（默认 1，首次含模型加载，必须预热）

            两个后端的准备：

              ① llama.cpp server（推荐，版本可控）
                 llama-server -hf ggml-org/Qwen3-ASR-1.7B-GGUF:Q8_0 --host 127.0.0.1 --port 8080
                 → asr-spike run --shape llama --base-url http://127.0.0.1:8080 --model qwen3-asr

              ② Ollama（社区包，注意单次 ≤ 60 秒、返回带 language 前缀）
                 ollama pull frozenlab/qwen3-asr:1.7b
                 → asr-spike run --shape ollama --model frozenlab/qwen3-asr:1.7b --duration 60

            退出码：0 成功 · 1 参数/IO 错误 · 2 HTTP 失败 · 3 解析出空文本
            """);
    }

    private static int Unknown(string cmd)
    {
        Err($"未知命令：{cmd}（试 --help）");
        return 1;
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(string[] args, int start)
        {
            var o = new Options();
            for (var i = start; i < args.Length; i++)
            {
                var a = args[i];
                if (!a.StartsWith("--", StringComparison.Ordinal))
                {
                    o._values[a] = "true";
                    continue;
                }

                var key = a[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    o._values[key] = args[++i];
                }
                else
                {
                    o._values[key] = "true";
                }
            }

            return o;
        }

        public string? Str(string key, string? fallback = null) =>
            _values.TryGetValue(key, out var v) && v is not null ? v : fallback;

        public bool Flag(string key) =>
            _values.TryGetValue(key, out var v) && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

        public int Int(string key, int fallback) =>
            int.TryParse(Str(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static ChatAudioShape Shape(Options o) => (o.Str("shape") ?? "llama").ToLowerInvariant() switch
    {
        "ollama" => ChatAudioShape.OllamaImages,
        "llama" or "llamacpp" or "llama.cpp" or "openai" or "server" => ChatAudioShape.OpenAiInputAudio,
        var other => throw new ArgumentException($"未知 --shape：{other}（llama | openai | ollama）"),
    };

    private static string DefaultBase(ChatAudioShape shape) =>
        shape == ChatAudioShape.OllamaImages ? "http://127.0.0.1:11434" : "http://127.0.0.1:8080";

    private static string Require(Options o, string key) =>
        o.Str(key) ?? throw new ArgumentException($"缺少 --{key}（试 --help）");

    private static void Cleanup(AudioPrep.Prepared prepared)
    {
        if (!prepared.Temporary) return;
        try { File.Delete(prepared.Path); } catch { /* 临时文件删不掉不影响结果 */ }
    }

    private static void AppendMarkdownRow(string path, string model, ChatAudioShape shape,
        double audioSeconds, TimeSpan elapsed, double rtf, ParseResult parsed)
    {
        var exists = File.Exists(path);
        var sb = new StringBuilder();
        if (!exists)
        {
            sb.AppendLine("| 模型 | 形态 | 音频 | 耗时 | RTF | 字符 | 告警 | 时间 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        }

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| {model} | {shape} | {audioSeconds:F0}s | {elapsed.TotalSeconds:F2}s | {rtf:F2}× | {parsed.Text.Length} | " +
            $"{(parsed.Warnings.Count == 0 ? "-" : string.Join(",", parsed.Warnings))} | {DateTime.Now:yyyy-MM-dd HH:mm} |"));

        File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void HintForHttp(ChatAudioException ex)
    {
        Console.Error.WriteLine();
        switch (ex.StatusCode)
        {
            case 404:
                Console.Error.WriteLine("404 常见原因：");
                Console.Error.WriteLine("  · llama.cpp：模型没加载（llama-server 启动日志里看 model loaded）");
                Console.Error.WriteLine("  · Ollama：模型名写错（先 ollama list）");
                Console.Error.WriteLine("  · --shape 与后端不匹配：Ollama 用 ollama，llama.cpp 用 llama");
                break;
            case 400:
                Console.Error.WriteLine("400 常见原因：");
                Console.Error.WriteLine("  · audio 字段形状不对（Ollama 用 images，llama.cpp 用 input_audio）");
                Console.Error.WriteLine("  · 音频超过单次上限（Ollama 社区包约 60 秒）");
                break;
            case 415 or 422:
                Console.Error.WriteLine("上游不收这个音频：确认是 16 kHz 单声道 16-bit WAV（本工具默认已转换）。");
                break;
            case 401 or 403:
                Console.Error.WriteLine("鉴权失败：本地服务一般不需要 Key；如果你加了 --api-key，去掉试试。");
                break;
        }
    }

    private static void Say(string message) => Console.WriteLine(message);
    private static void Warn(string message) => Console.WriteLine($"[warn] {message}");
    private static void Err(string message) => Console.Error.WriteLine($"[error] {message}");
}
