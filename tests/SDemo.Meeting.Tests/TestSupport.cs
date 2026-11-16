using Microsoft.Data.Sqlite;
using SDemo.App.Meeting.Storage;

namespace SDemo.Meeting.Tests;

/// <summary>每个测试一个临时库；用完必须 ClearAllPools，否则临时目录删不掉。</summary>
public sealed class TempDb : IDisposable
{
    private readonly string _dir;

    public TempDb()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meeting-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Database = MeetingDatabase.Open(Path.Combine(_dir, "meeting.db"));
        Session = new MeetingSession(Database);
    }

    public MeetingDatabase Database { get; }

    public MeetingSession Session { get; }

    public string Dir => _dir;

    public void Dispose()
    {
        Database.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 测试清理不抛 */ }
    }
}

/// <summary>生成合法的 16 kHz / 单声道 / 16-bit WAV（静音或简单波形），不需要 ffmpeg。</summary>
public static class TestAudio
{
    public static string WriteSilence(string path, int seconds = 1, short amplitude = 0)
    {
        var sampleRate = 16000;
        var samples = sampleRate * seconds;
        var bytes = samples * 2;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        w.Write("RIFF"u8.ToArray());
        w.Write((uint)(36 + bytes));
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16u);
        w.Write((ushort)1);
        w.Write((ushort)1);
        w.Write((uint)sampleRate);
        w.Write((uint)(sampleRate * 2));
        w.Write((ushort)2);
        w.Write((ushort)16);
        w.Write("data"u8.ToArray());
        w.Write((uint)bytes);

        for (var i = 0; i < samples; i++)
        {
            var v = amplitude == 0 ? 0 : (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * amplitude);
            w.Write(v);
        }

        return path;
    }
}
