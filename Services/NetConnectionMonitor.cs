using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace PingBoost.Services;

public sealed record ConnectionSnapshot(
    string Key,
    string Protocol,
    int ProcessId,
    string Process,
    string Local,
    string Remote,
    string Host,
    string State,
    double DownBytesPerSecond,
    double UpBytesPerSecond,
    long TotalDown,
    long TotalUp,
    long Retransmits,
    int Connections,
    bool Active);

/// <summary>
/// Live list of TCP connections and UDP flows with the process, server name and speed of each.
/// Connections come from the system TCP table (works without elevation); speeds and UDP flows come from
/// the kernel ETW network provider (administrator rights). Only addresses, ports and sizes are read.
/// </summary>
public sealed class NetConnectionMonitor : IDisposable
{
    private const int MaxFlows = 5000;
    private static readonly long TableGrace = 3 * Stopwatch.Frequency;
    private static readonly long TcpIdleLimit = 5 * Stopwatch.Frequency;
    private static readonly long UdpIdleLimit = 30 * Stopwatch.Frequency;
    private static readonly long ActiveWindow = 3 * Stopwatch.Frequency;
    private static readonly TimeSpan DnsRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<FlowKey, Flow> _flows = new();
    private volatile Dictionary<FlowKey, TcpConnectionEntry> _tcpTable = [];
    private volatile Dictionary<int, GameUdpEndpoint> _udpOwners = [];
    private CancellationTokenSource? _cancellation;
    private Task? _refresher;
    private bool _capturing;
    private int _dnsRefreshRunning;

    public bool IsRunning => _cancellation is not null;

    /// <summary>True when per-connection speeds are being captured.</summary>
    public bool IsCapturing => _capturing;

    /// <summary>Why capturing is unavailable (not elevated, session could not start); empty when it works.</summary>
    public string CaptureProblem { get; private set; } = string.Empty;

    public void Start()
    {
        if (IsRunning)
            return;
        _flows.Clear();
        _capturing = false;
        CaptureProblem = string.Empty;

        if (!KernelNetHub.IsElevated)
        {
            CaptureProblem = "NotElevated";
        }
        else
        {
            try
            {
                KernelNetHub.Acquire();
                KernelNetHub.Packet += OnPacket;
                _capturing = true;
            }
            catch (Exception ex)
            {
                // ETW can fail in many ways (another trace owns the session, policy); fall back to the table-only view.
                CaptureProblem = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        RefreshTables();
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        _ = HostNameResolver.RefreshDnsCacheAsync();
        var nextDns = DateTime.UtcNow + DnsRefreshInterval;
        _refresher = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(1), token); }
                catch (OperationCanceledException) { break; }
                RefreshTables();
                if (DateTime.UtcNow >= nextDns && Interlocked.Exchange(ref _dnsRefreshRunning, 1) == 0)
                {
                    nextDns = DateTime.UtcNow + DnsRefreshInterval;
                    _ = RefreshDnsAsync();
                }
            }
        });
    }

    private async Task RefreshDnsAsync()
    {
        try { await HostNameResolver.RefreshDnsCacheAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // powershell.exe is unavailable: names come from reverse DNS only.
        }
        finally { Interlocked.Exchange(ref _dnsRefreshRunning, 0); }
    }

    public void Stop()
    {
        if (_cancellation is null)
            return;
        if (_capturing)
        {
            KernelNetHub.Packet -= OnPacket;
            KernelNetHub.Release();
            _capturing = false;
        }
        _cancellation.Cancel();
        try { _refresher?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cancellation.Dispose();
        _cancellation = null;
        _refresher = null;
    }

    public void Dispose() => Stop();

    /// <summary>Current rows with speeds measured since the previous call; call about once a second.</summary>
    public IReadOnlyList<ConnectionSnapshot> Snapshot()
    {
        var now = Stopwatch.GetTimestamp();
        var rows = new List<ConnectionSnapshot>(_flows.Count);
        foreach (var (key, flow) in _flows)
        {
            if (flow.IsExpired(now))
            {
                _flows.TryRemove(key, out _);
                continue;
            }
            rows.Add(flow.Snapshot(now));
        }
        return rows;
    }

    private static bool IsIgnorable(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast))
            return true;
        if (address.IsIPv6Multicast || address.IsIPv6LinkLocal)
            return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] >= 224;
    }

    private void RefreshTables()
    {
        var now = Stopwatch.GetTimestamp();
        try
        {
            var entries = TcpTableService.GetConnections();
            var table = new Dictionary<FlowKey, TcpConnectionEntry>(entries.Count);
            foreach (var entry in entries)
            {
                // Listening sockets and connections in TIME_WAIT / DELETE_TCB are noise for this view.
                if (entry.State is 2 or 11 or 12)
                    continue;
                var remote = HostNameResolver.Normalize(entry.RemoteAddress);
                if (IsIgnorable(remote))
                    continue;
                var key = new FlowKey(true, entry.LocalPort, remote, entry.RemotePort);
                table[key] = entry;
                if (_flows.Count >= MaxFlows && !_flows.ContainsKey(key))
                    continue;
                _flows.GetOrAdd(key, k => new Flow(k, now)).UpdateFromTable(entry, now);
            }
            _tcpTable = table;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            // Keep the previous table; the next refresh will try again.
        }

        try
        {
            var owners = new Dictionary<int, GameUdpEndpoint>();
            foreach (var endpoint in GameProcessService.GetUdpEndpoints())
                owners[endpoint.LocalPort] = endpoint;
            _udpOwners = owners;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            // Keep the previous owner data.
        }
    }

    private void OnPacket(NetPacket packet)
    {
        var source = HostNameResolver.Normalize(packet.Source);
        var destination = HostNameResolver.Normalize(packet.Destination);

        // First candidate: the source is this PC (upload). Second: the destination is this PC (download).
        var uploadKey = new FlowKey(packet.IsTcp, packet.SourcePort, destination, packet.DestinationPort);
        var downloadKey = new FlowKey(packet.IsTcp, packet.DestinationPort, source, packet.SourcePort);

        bool upload;
        var ownerPid = 0;
        if (packet.IsTcp)
        {
            // The kernel event direction is not reliable for received packets, so the connection table decides.
            var table = _tcpTable;
            if (table.TryGetValue(uploadKey, out var entry))
            {
                upload = true;
                ownerPid = entry.ProcessId;
            }
            else if (table.TryGetValue(downloadKey, out entry))
            {
                upload = false;
                ownerPid = entry.ProcessId;
            }
            else
            {
                upload = packet.Outbound;
            }
        }
        else
        {
            var owners = _udpOwners;
            var sourceOwned = owners.TryGetValue(packet.SourcePort, out var sourceOwner);
            var destinationOwned = owners.TryGetValue(packet.DestinationPort, out var destinationOwner);
            if (sourceOwned && !destinationOwned)
            {
                upload = true;
                ownerPid = sourceOwner!.ProcessId;
            }
            else if (destinationOwned && !sourceOwned)
            {
                upload = false;
                ownerPid = destinationOwner!.ProcessId;
            }
            else
            {
                upload = packet.Outbound;
            }
        }

        var key = upload ? uploadKey : downloadKey;
        if (IsIgnorable(key.Remote))
            return;
        var now = Stopwatch.GetTimestamp();
        if (!_flows.TryGetValue(key, out var flow))
        {
            if (_flows.Count >= MaxFlows)
                return;
            flow = _flows.GetOrAdd(key, k => new Flow(k, now));
        }
        flow.SetProcess(ownerPid != 0 ? ownerPid : packet.ProcessId, upload ? source : destination);
        if (packet.Retransmit)
            flow.AddRetransmit();
        else
            flow.AddBytes(packet.Size, upload, now);
    }

    private readonly record struct FlowKey(bool IsTcp, int LocalPort, IPAddress Remote, int RemotePort);

    private sealed class Flow(FlowKey key, long created)
    {
        private long _down, _up, _retransmits, _lastTraffic, _lastInTable;
        private long _prevDown, _prevUp, _prevTick = created;
        private readonly long _created = created;
        private volatile int _pid;
        private volatile int _state;
        private volatile IPAddress? _localAddress;

        public void AddBytes(int size, bool upload, long now)
        {
            if (upload)
                Interlocked.Add(ref _up, size);
            else
                Interlocked.Add(ref _down, size);
            Interlocked.Exchange(ref _lastTraffic, now);
        }

        public void AddRetransmit() => Interlocked.Increment(ref _retransmits);

        public void SetProcess(int pid, IPAddress local)
        {
            if (pid > 0)
                _pid = pid;
            _localAddress = local;
        }

        public void UpdateFromTable(TcpConnectionEntry entry, long now)
        {
            if (entry.ProcessId > 0)
                _pid = entry.ProcessId;
            _state = entry.State;
            _localAddress = entry.LocalAddress;
            Interlocked.Exchange(ref _lastInTable, now);
        }

        public bool IsExpired(long now)
        {
            var lastActivity = Math.Max(Interlocked.Read(ref _lastTraffic), _created);
            if (key.IsTcp)
                return now - Interlocked.Read(ref _lastInTable) > TableGrace && now - lastActivity > TcpIdleLimit;
            return now - lastActivity > UdpIdleLimit;
        }

        public ConnectionSnapshot Snapshot(long now)
        {
            var down = Interlocked.Read(ref _down);
            var up = Interlocked.Read(ref _up);
            var elapsed = Math.Max(0.5, (now - _prevTick) / (double)Stopwatch.Frequency);
            var downRate = (down - _prevDown) / elapsed;
            var upRate = (up - _prevUp) / elapsed;
            (_prevDown, _prevUp, _prevTick) = (down, up, now);

            var pid = _pid;
            var lastTraffic = Interlocked.Read(ref _lastTraffic);
            var local = _localAddress;
            return new ConnectionSnapshot(
                Key: $"{(key.IsTcp ? 'T' : 'U')}|{key.LocalPort}|{key.Remote}|{key.RemotePort}",
                Protocol: key.IsTcp ? "TCP" : "UDP",
                ProcessId: pid,
                Process: pid > 0 ? GameProcessService.ResolveName(pid) : "—",
                Local: local is null ? $":{key.LocalPort}" : Endpoint(local, key.LocalPort),
                Remote: Endpoint(key.Remote, key.RemotePort),
                Host: HostNameResolver.Lookup(key.Remote),
                State: key.IsTcp ? StateName(_state) : string.Empty,
                DownBytesPerSecond: downRate,
                UpBytesPerSecond: upRate,
                TotalDown: down,
                TotalUp: up,
                Retransmits: Interlocked.Read(ref _retransmits),
                Connections: 1,
                Active: lastTraffic != 0 && now - lastTraffic < ActiveWindow);
        }

        private static string Endpoint(IPAddress address, int port)
            => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

        private static string StateName(int state) => state switch
        {
            1 => "CLOSED",
            3 => "SYN_SENT",
            4 => "SYN_RCVD",
            5 => "ESTABLISHED",
            6 => "FIN_WAIT1",
            7 => "FIN_WAIT2",
            8 => "CLOSE_WAIT",
            9 => "CLOSING",
            10 => "LAST_ACK",
            _ => string.Empty
        };
    }
}
