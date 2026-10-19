using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace PingBoost.Services;

public sealed record GameUdpEndpoint(int ProcessId, string ProcessName, string LocalAddress, int LocalPort);

public static class GameProcessService
{
    private const int AfInet = 2;
    private const int UdpTableOwnerPid = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct UdpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint ProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    private static readonly TimeSpan NameCacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly Dictionary<int, (string Name, DateTime Resolved)> NameCache = [];

    /// <summary>The table is polled every second by the UDP monitor, so names are cached and handles are released.</summary>
    private static string ResolveName(int pid)
    {
        lock (NameCache)
        {
            if (NameCache.TryGetValue(pid, out var cached) && DateTime.UtcNow - cached.Resolved < NameCacheLifetime)
                return cached.Name;
            string name;
            try
            {
                using var process = Process.GetProcessById(pid);
                name = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                name = $"PID {pid}";
            }
            NameCache[pid] = (name, DateTime.UtcNow);
            return name;
        }
    }

    public static IReadOnlyList<GameUdpEndpoint> GetUdpEndpoints()
    {
        // The table can grow between the size query and the read (ERROR_INSUFFICIENT_BUFFER), so retry a few times.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var size = 0;
            _ = GetExtendedUdpTable(IntPtr.Zero, ref size, true, AfInet, UdpTableOwnerPid, 0);
            if (size <= 0)
                return [];
            var endpoints = ReadUdpTable(size + 4096 * attempt);
            if (endpoints is not null)
                return endpoints;
        }
        return [];
    }

    private static List<GameUdpEndpoint>? ReadUdpTable(int size)
    {
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = GetExtendedUdpTable(buffer, ref size, true, AfInet, UdpTableOwnerPid, 0);
            if (result != 0)
                return null;

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<UdpRowOwnerPid>();
            var endpoints = new List<GameUdpEndpoint>(count);
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<UdpRowOwnerPid>(IntPtr.Add(buffer, sizeof(uint) + index * rowSize));
                var pid = unchecked((int)row.ProcessId);
                var processName = ResolveName(pid);
                var address = new IPAddress(row.LocalAddress).ToString();
                var networkPort = BitConverter.GetBytes(row.LocalPort);
                var port = (networkPort[0] << 8) | networkPort[1];
                endpoints.Add(new GameUdpEndpoint(pid, processName, address, port));
            }
            return endpoints;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
