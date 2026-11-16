using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SDemo.App.Meeting.Contracts;

namespace SDemo.App.Meeting.Audio;

/// <summary>
/// 把任意 PCM/浮点 WAV 转成 16 kHz / 单声道 / 16-bit PCM。
/// 采集端用设备原生格式落盘（兼容性最好），转码在这一步做。
/// </summary>
public static class WavDownmixer
{
    public static WavFormat ToTargetFormat(string sourcePath, string destPath)
    {
        using var reader = new AudioFileReader(sourcePath);
        ISampleProvider samples = reader;

        if (reader.WaveFormat.SampleRate != MeetingConstants.TargetSampleRate)
            samples = new WdlResamplingSampleProvider(samples, MeetingConstants.TargetSampleRate);

        if (samples.WaveFormat.Channels > 1)
            samples = new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
        WaveFileWriter.CreateWaveFile16(destPath, samples);

        return WavFormatReader.Read(destPath);
    }
}
