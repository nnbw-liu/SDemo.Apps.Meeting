using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

/// <summary>一段"有人在说话"的时间窗。</summary>
public sealed record VadSegment(int StartMs, int EndMs)
{
    public int DurationMs => EndMs - StartMs;

    public override string ToString() => $"[{StartMs}..{EndMs}] {DurationMs}ms";
}

/// <summary>
/// 能量型 VAD：不引模型、不引依赖，纯信号处理。
///
/// 为什么不用 nnnoiseless / silero：VAD 只是"把长音频切成 20–60 秒的块"，
/// 切错一点不影响最终转写质量（每段仍然完整），但引一个模型要多几百 MB 和一条推理链。
///
/// 算法：20 ms 一帧算 RMS → 估噪声底（低分位）→ 双阈值（迟滞）判语音 →
/// 合并短间隙、丢弃短片段 → 超长片段在最低能量处切开。
/// </summary>
public static class VadSegmenter
{
    public const int FrameMs = 20;

    public sealed record Options(
        int MaxSegmentMs = 60_000,
        int MinSegmentMs = 500,
        int MergeGapMs = 400,
        int MaxPackGapMs = 4_000,
        int PaddingMs = 150,
        double NoisePercentile = 0.2,
        double StartFactor = 3.0,
        double KeepFactor = 1.6,
        int MinSilenceMs = 250);

    /// <summary>把 16 kHz 单声道 PCM16 的样本切成语音窗。</summary>
    public static IReadOnlyList<VadSegment> Segment(ReadOnlySpan<short> samples, Options? options = null)
    {
        var opt = options ?? new Options();
        var sampleRate = MeetingConstants.TargetSampleRate;
        var frameSize = sampleRate * FrameMs / 1000;
        var frameCount = samples.Length / frameSize;

        if (frameCount == 0)
            return samples.Length == 0 ? Array.Empty<VadSegment>() : new[] { new VadSegment(0, ToMs(samples.Length, sampleRate)) };

        var rms = new double[frameCount];
        for (var f = 0; f < frameCount; f++)
        {
            double sum = 0;
            var start = f * frameSize;
            for (var i = 0; i < frameSize; i++)
            {
                double v = samples[start + i];
                sum += v * v;
            }

            rms[f] = Math.Sqrt(sum / frameSize);
        }

        var sorted = rms.OrderBy(x => x).ToArray();
        var maxRms = sorted[^1];
        if (maxRms < 100) return Array.Empty<VadSegment>(); // 整段几乎无声，不必假装有语音

        var noise = sorted[Math.Clamp((int)(sorted.Length * opt.NoisePercentile), 0, sorted.Length - 1)];

        double startThreshold, keepThreshold;
        if (noise > maxRms * 0.6)
        {
            // 没有清晰静音底噪（比如整段都是说话）：退回按能量比例设阈值，
            // 否则自适应阈值会把整段判成"没有语音"。
            startThreshold = Math.Max(120, maxRms * 0.15);
            keepThreshold = Math.Max(80, startThreshold * 0.6);
        }
        else
        {
            startThreshold = Math.Max(noise * opt.StartFactor, 120);
            keepThreshold = Math.Max(noise * opt.KeepFactor, 80);
        }

        var regions = new List<(int StartFrame, int EndFrame)>();
        var inSpeech = false;
        var begin = 0;
        var silenceFrames = 0;
        var minSilenceFrames = Math.Max(1, opt.MinSilenceMs / FrameMs);

        for (var f = 0; f < frameCount; f++)
        {
            var isLoud = inSpeech ? rms[f] >= keepThreshold : rms[f] >= startThreshold;

            if (isLoud)
            {
                if (!inSpeech)
                {
                    inSpeech = true;
                    begin = f;
                }

                silenceFrames = 0;
            }
            else if (inSpeech)
            {
                silenceFrames++;
                if (silenceFrames >= minSilenceFrames)
                {
                    regions.Add((begin, f - silenceFrames));
                    inSpeech = false;
                    silenceFrames = 0;
                }
            }
        }

        if (inSpeech) regions.Add((begin, frameCount - 1));

        var merged = Merge(regions, opt.MergeGapMs);

        // 先转成毫秒并加 padding，过滤太短的
        var raw = new List<(int Start, int End)>();
        foreach (var (startFrame, endFrame) in merged)
        {
            var startMs = Math.Max(0, startFrame * FrameMs - opt.PaddingMs);
            var endMs = Math.Min(ToMs(samples.Length, sampleRate), (endFrame + 1) * FrameMs + opt.PaddingMs);
            if (endMs - startMs < opt.MinSegmentMs) continue;
            raw.Add((startMs, endMs));
        }

        // 窗口打包：VAD 会在每个自然停顿处断开（一句话一段），
        // 逐句调用模型等于每段都重跑一次音频编码，既慢又丢上下文。
        // 这里把"停顿不长 + 总长不超上限"的相邻段并回一个窗口。
        var packed = Pack(raw, opt);

        var segments = new List<VadSegment>();
        foreach (var (startMs, endMs) in packed)
        {
            foreach (var part in SplitLong(startMs, endMs, rms, opt)) segments.Add(part);
        }

        // 不能再做一次"相邻合并"：SplitLong 切出来的块本来就首尾相接，
        // 合并会把它们重新粘回一整段（上限就失效了）。
        return segments;
    }

    private static List<(int Start, int End)> Pack(List<(int Start, int End)> raw, Options opt)
    {
        var result = new List<(int Start, int End)>();

        foreach (var (start, end) in raw)
        {
            if (result.Count > 0)
            {
                var last = result[^1];
                var gap = start - last.End;
                var span = end - last.Start;

                if (gap <= opt.MaxPackGapMs && span <= opt.MaxSegmentMs)
                {
                    result[^1] = (last.Start, end);
                    continue;
                }
            }

            result.Add((start, end));
        }

        return result;
    }

    private static List<(int Start, int End)> Merge(List<(int StartFrame, int EndFrame)> regions, int mergeGapMs)
    {
        var gapFrames = mergeGapMs / FrameMs;
        var result = new List<(int, int)>();

        foreach (var region in regions)
        {
            if (result.Count > 0 && region.StartFrame - result[^1].Item2 <= gapFrames)
                result[^1] = (result[^1].Item1, region.EndFrame);
            else
                result.Add(region);
        }

        return result;
    }

    /// <summary>超过上限的片段，在"窗口内最安静的位置"切开，而不是硬切。</summary>
    private static IEnumerable<VadSegment> SplitLong(int startMs, int endMs, double[] rms, Options opt)
    {
        var cursor = startMs;
        while (endMs - cursor > opt.MaxSegmentMs)
        {
            var limit = cursor + opt.MaxSegmentMs;
            var searchStart = cursor + opt.MaxSegmentMs * 6 / 10; // 后 40% 里找最安静处
            var bestMs = limit;
            var bestRms = double.MaxValue;

            for (var t = searchStart; t <= limit; t += FrameMs)
            {
                var f = t / FrameMs;
                if (f >= rms.Length) break;
                if (rms[f] < bestRms)
                {
                    bestRms = rms[f];
                    bestMs = t;
                }
            }

            yield return new VadSegment(cursor, bestMs);
            cursor = bestMs;
        }

        if (endMs > cursor) yield return new VadSegment(cursor, endMs);
    }

    private static int ToMs(int samples, int sampleRate) => (int)Math.Round(samples * 1000.0 / sampleRate);

    /// <summary>从 16 kHz 单声道 PCM16 WAV 读样本（只解析 RIFF 头，不做重采样）。</summary>
    public static short[] ReadSamples(string wavPath)
    {
        var format = WavFormatReader.Read(wavPath);
        if (!format.IsTarget)
            throw new MeetingException(MeetingErrorCodes.AudioUnreadable,
                $"VAD 只接受 16 kHz / 单声道 / 16-bit：{wavPath} 是 {format}");

        var bytes = File.ReadAllBytes(wavPath);
        var dataOffset = FindDataOffset(bytes);
        var count = (bytes.Length - dataOffset) / 2;
        var samples = new short[count];
        Buffer.BlockCopy(bytes, dataOffset, samples, 0, count * 2);
        return samples;
    }

    private static int FindDataOffset(byte[] bytes)
    {
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "data") return pos + 8;
            pos += 8 + size + (size % 2);
        }

        throw new InvalidDataException("WAV 没有 data 块");
    }
}
