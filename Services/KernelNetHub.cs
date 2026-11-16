using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace PingBoost.Services;

/// <summary>One TCP or UDP packet seen by the Windows kernel network provider (metadata only).</summary>
public readonly record struct NetPacket(
    bool IsTcp,
    IPAddress Source,
    int SourcePort,
    IPAddress Destination,
    int DestinationPort,
    int Size,
    int ProcessId,
    bool Outbound,
    bool Retransmit);

/// <summary>
/// Owns the single "NT Kernel Logger" ETW session and shares it between monitors (UDP monitor, connection monitor).
/// The session runs while at least one monitor holds it. Only packet metadata is read; payloads are never inspected.
/// </summary>
public static class KernelNetHub
{
    private static readonly object Gate = new();
    private static TraceEventSession? _session;
    private static Task? _worker;
    private static int _users;

    /// <summary>Raised on the ETW thread for every packet; handlers must be fast and must not block.</summary>
    public static event Action<NetPacket>? Packet;

    public static bool IsElevated => TraceEventSession.IsElevated() == true;

    public static void Acquire()
    {
        lock (Gate)
        {
            if (_users == 0)
                StartSession();
            _users++;
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            if (_users == 0)
                return;
            _users--;
            if (_users == 0)
                StopSession();
        }
    }

    private static void StartSession()
    {
        if (!IsElevated)
            throw new UnauthorizedAccessException("Для чтения сетевых событий Windows нужны права администратора.");

        var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName);
        try
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
        }
        catch
        {
            // Do not leave a half-configured kernel session running.
            session.Dispose();
            throw;
        }

        var kernel = session.Source.Kernel;
        kernel.UdpIpSend += d => Raise(false, d.saddr, d.sport, d.daddr, d.dport, d.size, d.ProcessID, true, false);
        kernel.UdpIpRecv += d => Raise(false, d.saddr, d.sport, d.daddr, d.dport, d.size, d.ProcessID, false, false);
        kernel.UdpIpSendIPV6 += d => Raise(false, d.saddr, d.sport, d.daddr, d.dport, d.size, d.ProcessID, true, false);
        kernel.UdpIpRecvIPV6 += d => Raise(false, d.saddr, d.sport, d.daddr, d.dport, d.size, d.ProcessID, false, false);
        kernel.TcpIpSend += d => Raise(true, d.saddr, d.sport, d.daddr, d.dport, d.size, d.ProcessID, true, false);
        kernel.TcpIpRecv += d => Raise(true, d.saddr, d.sport, d.daddr, d.dport, d.size, d.ProcessID, false, false);
        kernel.TcpIpSendIPV6 += d => Raise(true, d.saddr, d.sport, d.daddr, d.dport, d.size, d.ProcessID, true, false);
        kernel.TcpIpRecvIPV6 += d => Raise(true, d.saddr, d.sport, d.daddr, d.dport, d.size, d.ProcessID, false, false);
        kernel.TcpIpRetransmit += d => Raise(true, d.saddr, d.sport, d.daddr, d.dport, d.size, d.ProcessID, true, true);
        kernel.TcpIpRetransmitIPV6 += d => Raise(true, d.saddr, d.sport, d.daddr, d.dport, d.size, d.ProcessID, true, true);

        _session = session;
        _worker = Task.Run(() => session.Source.Process());
    }

    private static void StopSession()
    {
        var session = _session;
        _session = null;
        session?.Dispose();
        try { _worker?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
        _worker = null;
    }

    private static void Raise(bool tcp, IPAddress source, int sourcePort, IPAddress destination, int destinationPort,
        int size, int processId, bool outbound, bool retransmit)
        => Packet?.Invoke(new NetPacket(tcp, source, sourcePort, destination, destinationPort, size, processId, outbound, retransmit));
}
