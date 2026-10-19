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

    public static IReadOnlyList<GameUdpEndpoint> GetUdpEndpoints()
    {
        var size = 0;
        _ = GetExtendedUdpTable(IntPtr.Zero, ref size, true, AfInet, UdpTableOwnerPid, 0);
        if (size <= 0)
            return [];

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = GetExtendedUdpTable(buffer, ref size, true, AfInet, UdpTableOwnerPid, 0);
            if (result != 0)
                return [];

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<UdpRowOwnerPid>();
            var endpoints = new List<GameUdpEndpoint>(count);
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<UdpRowOwnerPid>(IntPtr.Add(buffer, sizeof(uint) + index * rowSize));
                var pid = unchecked((int)row.ProcessId);
                string processName;
                try
                {
                    processName = Process.GetProcessById(pid).ProcessName;
                }
                catch (ArgumentException)
                {
                    processName = $"PID {pid}";
                }
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
