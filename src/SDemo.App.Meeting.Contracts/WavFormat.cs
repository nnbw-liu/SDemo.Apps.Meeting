namespace SDemo.App.Meeting.Contracts;

public sealed record WavFormat(int SampleRate, int Channels, int BitsPerSample, long DataBytes)
{
    public int BlockAlign => Math.Max(1, Channels * (BitsPerSample / 8));

    public double Seconds => DataBytes / (double)(SampleRate * BlockAlign);

    public long DurationMs => (long)Math.Round(Seconds * 1000);

    public bool IsTarget =>
        SampleRate == MeetingConstants.TargetSampleRate &&
        Channels == MeetingConstants.TargetChannels &&
        BitsPerSample == MeetingConstants.TargetBitsPerSample;

    public override string ToString() => $"{SampleRate} Hz · {Channels} ch · {BitsPerSample}-bit";
}

/// <summary>只解析 RIFF/WAVE 头，不读样本。任何 PCM WAV 都能读。</summary>
public static class WavFormatReader
{
    public static WavFormat Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException($"不是 RIFF/WAV：{path}");
        _ = br.ReadUInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException($"不是 WAVE：{path}");

        int rate = 0, channels = 0, bits = 0;
        long data = 0;

        while (fs.Position + 8 <= fs.Length)
        {
            var id = new string(br.ReadChars(4));
            var size = br.ReadUInt32();
            var next = fs.Position + size + (size % 2);

            if (id == "fmt ")
            {
                _ = br.ReadUInt16();
                channels = br.ReadUInt16();
                rate = br.ReadInt32();
                _ = br.ReadInt32();
                _ = br.ReadUInt16();
                bits = br.ReadUInt16();
            }
            else if (id == "data")
            {
                data = Math.Min(size, (uint)Math.Max(0, fs.Length - fs.Position));
            }

            if (next > fs.Length) break;
            fs.Position = Math.Max(next, fs.Position);
        }

        if (rate == 0 || channels == 0) throw new InvalidDataException($"WAV 缺少 fmt 块：{path}");
        return new WavFormat(rate, channels, bits, data);
    }

    public static bool TryRead(string path, out WavFormat? format, out string? error)
    {
        try
        {
            format = Read(path);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            format = null;
            error = ex.Message;
            return false;
        }
    }
}
