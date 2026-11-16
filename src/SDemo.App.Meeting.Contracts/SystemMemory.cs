using System.Runtime.InteropServices;

namespace SDemo.App.Meeting.Contracts;

public sealed record MemorySnapshot(long TotalBytes, long AvailableBytes)
{
    public double TotalGb => TotalBytes / 1024d / 1024d / 1024d;

    public double AvailableGb => AvailableBytes / 1024d / 1024d / 1024d;
}

/// <summary>
/// 读本机内存。Windows 走 GlobalMemoryStatusEx（拿到的是"真的空闲物理内存"），
/// 其它平台退回 GC 的可用上限——只用于"要不要降档"的粗判，不追求精确。
/// </summary>
public static class SystemMemory
{
    /// <summary>模型之外还要留给运行时 / 音频缓冲的余量。</summary>
    public const long HeadroomBytes = 512L * 1024 * 1024;

    public static MemorySnapshot Read()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref status))
                    return new MemorySnapshot((long)status.TotalPhys, (long)status.AvailPhys);
            }
            catch (DllNotFoundException)
            {
                // 落到下面的通用路径
            }
            catch (EntryPointNotFoundException)
            {
                // 同上
            }
        }

        var info = GC.GetGCMemoryInfo();
        var total = info.TotalAvailableMemoryBytes;
        var used = GC.GetTotalMemory(forceFullCollection: false);
        return new MemorySnapshot(total, Math.Max(0, total - used));
    }

    /// <summary>这个模型按当前内存能不能装下。</summary>
    public static bool Fits(long requiredBytes, MemorySnapshot snapshot) =>
        requiredBytes <= 0 || snapshot.AvailableBytes >= requiredBytes + HeadroomBytes;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
