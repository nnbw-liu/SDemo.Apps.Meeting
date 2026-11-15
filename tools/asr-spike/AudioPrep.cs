using System.Diagnostics;
using System.Globalization;

namespace AsrSpike;

/// <summary>
/// 把任意输入音频准备成 16 kHz / 单声道 / 16-bit PCM WAV。
/// 有 ffmpeg 就用它（精度与格式覆盖都靠它）；没有 ffmpeg 时只接受已经合规的 WAV。
/// </summary>
public static class AudioPrep
{
    public sealed record WavInfo(int SampleRate, int Channels, int BitsPerSample, long DataBytes)
    {
        public double Seconds => DataBytes / (double)(SampleRate * Channels * Math.Max(1, BitsPerSample / 8));

        public bool IsTarget => SampleRate == 16000 && Channels == 1 && BitsPerSample == 16;

        public override string ToString() =>
            $"{SampleRate} Hz · {Channels} ch · {BitsPerSample}-bit · {Seconds:F1}s";
    }

    public sealed record Prepared(string Path, bool Temporary, WavInfo Info);

    /// <summary>在 PATH 里找 ffmpeg；找不到返回 null（不要抛，调用方要能给出可读提示）。</summary>
    public static string? FindFfmpeg(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // PATH 里可能有非法项，跳过
                }
            }
        }

        return null;
    }

    public static WavInfo ReadWav(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("不是 RIFF/WAV 文件");
        _ = br.ReadUInt32(); // riff size
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("不是 WAVE 格式");

        int rate = 0, channels = 0, bits = 0;
        long data = 0;

        while (fs.Position + 8 <= fs.Length)
        {
            var id = new string(br.ReadChars(4));
            var size = br.ReadUInt32();
            var next = fs.Position + size + (size % 2); // chunk 按偶数字节对齐

            if (id == "fmt ")
            {
                _ = br.ReadUInt16();          // format tag
                channels = br.ReadUInt16();
                rate = br.ReadInt32();
                _ = br.ReadInt32();           // byte rate
                _ = br.ReadUInt16();          // block align
                bits = br.ReadUInt16();
            }
            else if (id == "data")
            {
                data = Math.Min(size, (uint)Math.Max(0, fs.Length - fs.Position));
            }

            if (next > fs.Length) break;
            fs.Position = Math.Max(next, fs.Position); // 恰好读完一块时 next == 当前位置，要继续往下走
        }

        if (rate == 0 || channels == 0) throw new InvalidDataException("WAV 缺少 fmt 块");
        return new WavInfo(rate, channels, bits, data);
    }

    /// <summary>
    /// 准备一段音频。start / duration 为 null 时表示整段。
    /// </summary>
    public static Prepared Prepare(
        string input,
        TimeSpan? start,
        TimeSpan? duration,
        string? ffmpegPath,
        string? outputPath = null,
        Action<string>? log = null)
    {
        if (!File.Exists(input)) throw new FileNotFoundException($"找不到输入音频：{input}");

        var ffmpeg = FindFfmpeg(ffmpegPath);
        var temp = false;

        if (ffmpeg is not null)
        {
            var outPath = outputPath ?? Path.Combine(Path.GetTempPath(), $"asrspike-{Guid.NewGuid():N}.wav");
            temp = outputPath is null;

            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", input };
            if (start is { } s && s > TimeSpan.Zero)
            {
                args.Add("-ss");
                args.Add(s.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
            }

            if (duration is { } d && d > TimeSpan.Zero)
            {
                args.Add("-t");
                args.Add(d.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
            }

            args.AddRange(new[] { "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav", outPath });

            log?.Invoke($"ffmpeg: {ffmpeg}");
            RunProcess(ffmpeg, args);
            var info = ReadWav(outPath);
            log?.Invoke($"已准备：{outPath}（{info}）");
            return new Prepared(outPath, temp, info);
        }

        // 没有 ffmpeg：只接受已经合规的 WAV
        var source = ReadWav(input);
        log?.Invoke($"未找到 ffmpeg，直接使用原文件：{source}");
        if (!source.IsTarget)
            throw new InvalidOperationException(
                $"未找到 ffmpeg，且输入不是 16 kHz / 单声道 / 16-bit WAV（当前 {source}）。\n" +
                "装一个 ffmpeg 并加入 PATH，或用 --ffmpeg <路径> 指定；也可以先用别的工具转成 16k 单声道 WAV。");

        if (start is { } ss && ss > TimeSpan.Zero || duration is { } dd && dd > TimeSpan.Zero)
            throw new InvalidOperationException("未找到 ffmpeg，无法裁剪片段；请先自行切好 WAV 再喂。");

        return new Prepared(input, false, source);
    }

    public static void RunProcess(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"启动失败：{exe}");
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} 退出码 {p.ExitCode}：{stderr.Trim()}");
    }

    /// <summary>接受 "12:31" / "00:12:31" / "751.5" / "751s"。</summary>
    public static TimeSpan? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimEnd('s', 'S');

        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            double total = 0;
            foreach (var p in parts)
            {
                if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    throw new FormatException($"无法解析时间：{text}");
                total = total * 60 + v;
            }

            return TimeSpan.FromSeconds(total);
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
            return TimeSpan.FromSeconds(sec);

        throw new FormatException($"无法解析时间：{text}");
    }
}
