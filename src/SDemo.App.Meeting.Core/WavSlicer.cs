using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Core;

/// <summary>
/// 把一段 16 kHz 单声道 PCM16 样本写成合法的 WAV 文件。
/// 给"必须按段送"的引擎用：chat-audio、DeepSeek tools（≤120 秒）都吃整文件。
/// </summary>
public static class WavSlicer
{
    public static void WriteSlice(string path, short[] samples, int startMs, int endMs)
    {
        var rate = MeetingConstants.TargetSampleRate;
        var startSample = Math.Clamp(startMs * rate / 1000, 0, samples.Length);
        var endSample = Math.Clamp(endMs * rate / 1000, startSample, samples.Length);
        var count = endSample - startSample;
        var dataBytes = count * 2;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        w.Write("RIFF"u8.ToArray());
        w.Write((uint)(36 + dataBytes));
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16u);
        w.Write((ushort)1);                 // PCM
        w.Write((ushort)1);                 // mono
        w.Write((uint)rate);
        w.Write((uint)(rate * 2));          // byte rate
        w.Write((ushort)2);                 // block align
        w.Write((ushort)16);                // bits
        w.Write("data"u8.ToArray());
        w.Write((uint)dataBytes);

        var buffer = new byte[dataBytes];
        if (dataBytes > 0) Buffer.BlockCopy(samples, startSample * 2, buffer, 0, dataBytes);
        w.Write(buffer);
    }

    /// <summary>写一个临时切片，返回路径；调用方负责删。</summary>
    public static string WriteTemporary(short[] samples, int startMs, int endMs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"meeting-slice-{Guid.NewGuid():N}.wav");
        WriteSlice(path, samples, startMs, endMs);
        return path;
    }
}
