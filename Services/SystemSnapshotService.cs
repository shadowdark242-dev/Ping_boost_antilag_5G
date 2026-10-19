using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PingBoost.Services;

public sealed record SystemSnapshot(double CpuPercent, ulong AvailableMemoryBytes, long NetworkErrors, long NetworkDiscards);

public sealed class SystemSnapshotService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    private ulong? _previousIdle;
    private ulong? _previousTotal;

    public SystemSnapshot Read()
    {
        var cpu = ReadCpuPercent();
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        _ = GlobalMemoryStatusEx(ref memory);
        long errors = 0;
        long discards = 0;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up))
        {
            try
            {
                var stats = adapter.GetIPStatistics();
                errors += stats.IncomingPacketsWithErrors + stats.OutgoingPacketsWithErrors;
                discards += stats.IncomingPacketsDiscarded + stats.OutgoingPacketsDiscarded;
            }
            catch (NetworkInformationException) { }
        }
        return new SystemSnapshot(cpu, memory.AvailablePhysical, errors, discards);
    }

    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;
        var idleValue = ToUInt64(idle);
        var totalValue = ToUInt64(kernel) + ToUInt64(user);
        double result = 0;
        if (_previousIdle is { } oldIdle && _previousTotal is { } oldTotal && totalValue > oldTotal)
        {
            var totalDelta = totalValue - oldTotal;
            var idleDelta = idleValue - oldIdle;
            result = Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100);
        }
        _previousIdle = idleValue;
        _previousTotal = totalValue;
        return result;
    }

    private static ulong ToUInt64(FileTime time) => ((ulong)time.High << 32) | time.Low;
}
