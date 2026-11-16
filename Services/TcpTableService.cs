using System.Net;
using System.Runtime.InteropServices;

namespace PingBoost.Services;

public sealed record TcpConnectionEntry(
    int ProcessId,
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress RemoteAddress,
    int RemotePort,
    int State);

/// <summary>Reads the system TCP connection table (IPv4 and IPv6) with the owning process of every connection.</summary>
public static class TcpTableService
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 x DWORD).
    private const int RowSizeV4 = 24;
    // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid.
    private const int RowSizeV6 = 56;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    public static IReadOnlyList<TcpConnectionEntry> GetConnections()
    {
        var result = new List<TcpConnectionEntry>();
        Read(AfInet, RowSizeV4, result);
        Read(AfInet6, RowSizeV6, result);
        return result;
    }

    private static void Read(int family, int rowSize, List<TcpConnectionEntry> into)
    {
        // The table can grow between the size query and the read (ERROR_INSUFFICIENT_BUFFER), so retry a few times.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var size = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0);
            if (size <= 0)
                return;
            size += 8192 * (attempt + 1);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0) != 0)
                    continue;
                var count = Marshal.ReadInt32(buffer);
                for (var index = 0; index < count; index++)
                {
                    var row = IntPtr.Add(buffer, sizeof(uint) + index * rowSize);
                    into.Add(family == AfInet ? ParseV4(row) : ParseV6(row));
                }
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static TcpConnectionEntry ParseV4(IntPtr row)
    {
        var state = Marshal.ReadInt32(row, 0);
        var local = new IPAddress((uint)Marshal.ReadInt32(row, 4));
        var localPort = Port(Marshal.ReadInt32(row, 8));
        var remote = new IPAddress((uint)Marshal.ReadInt32(row, 12));
        var remotePort = Port(Marshal.ReadInt32(row, 16));
        var pid = Marshal.ReadInt32(row, 20);
        return new TcpConnectionEntry(pid, local, localPort, remote, remotePort, state);
    }

    private static TcpConnectionEntry ParseV6(IntPtr row)
    {
        var localBytes = new byte[16];
        var remoteBytes = new byte[16];
        Marshal.Copy(row, localBytes, 0, 16);
        var localPort = Port(Marshal.ReadInt32(row, 20));
        Marshal.Copy(IntPtr.Add(row, 24), remoteBytes, 0, 16);
        var remotePort = Port(Marshal.ReadInt32(row, 44));
        var state = Marshal.ReadInt32(row, 48);
        var pid = Marshal.ReadInt32(row, 52);
        return new TcpConnectionEntry(pid, new IPAddress(localBytes), localPort, new IPAddress(remoteBytes), remotePort, state);
    }

    /// <summary>The port sits in the low 16 bits of the DWORD in network byte order.</summary>
    private static int Port(int value) => ((value & 0xFF) << 8) | ((value >> 8) & 0xFF);
}
