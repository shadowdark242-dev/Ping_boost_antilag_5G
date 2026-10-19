using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace PingBoost.Services;

public sealed record UdpFlowRow(
    string ProcessName,
    string Remote,
    long SentPackets,
    long ReceivedPackets,
    string SentBytesText,
    string ReceivedBytesText,
    string ReceiveRateText,
    string MaxGapText,
    string LastReceiveText);

/// <summary>
/// Passive UDP statistics per remote endpoint from the Windows kernel ETW network provider.
/// Only packet metadata (addresses, ports, sizes, timing) is read; payloads are never inspected.
/// The process is attributed through the UDP owner table (local port → PID), because the kernel
/// event header is not always reliable for received packets.
/// </summary>
public sealed class UdpFlowMonitor : IDisposable
{
    private const int WindowSeconds = 10;
    private static readonly int[] NoisePorts = [53, 67, 68, 123, 137, 138, 1900, 5353, 5355];

    private readonly ConcurrentDictionary<(int Pid, string Remote), Flow> _flows = new();
    private volatile Dictionary<int, GameUdpEndpoint> _portOwners = [];
    private TraceEventSession? _session;
    private Task? _worker;
    private Task? _refresher;
    private CancellationTokenSource? _cancellation;
    private string _processFilter = string.Empty;

    public bool IsRunning => _session is not null;

    public static bool IsElevated => TraceEventSession.IsElevated() == true;

    public void Start(string processName)
    {
        if (IsRunning)
            return;
        if (!IsElevated)
            throw new UnauthorizedAccessException("Для чтения сетевых событий Windows нужны права администратора.");

        _flows.Clear();
        _processFilter = Path.GetFileNameWithoutExtension(processName.Trim());
        _portOwners = BuildPortMap();
        _cancellation = new CancellationTokenSource();

        var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName);
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
        var kernel = session.Source.Kernel;
        kernel.UdpIpSend += data => OnPacket(data.saddr, data.sport, data.daddr, data.dport, data.size, data.ProcessID, outbound: true);
        kernel.UdpIpRecv += data => OnPacket(data.saddr, data.sport, data.daddr, data.dport, data.size, data.ProcessID, outbound: false);
        kernel.UdpIpSendIPV6 += data => OnPacket(data.saddr, data.sport, data.daddr, data.dport, data.size, data.ProcessID, outbound: true);
        kernel.UdpIpRecvIPV6 += data => OnPacket(data.saddr, data.sport, data.daddr, data.dport, data.size, data.ProcessID, outbound: false);
        _session = session;
        _worker = Task.Run(() => session.Source.Process());

        var token = _cancellation.Token;
        _refresher = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(1), token); }
                catch (OperationCanceledException) { break; }
                _portOwners = BuildPortMap();
            }
        });
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        var session = _session;
        _session = null;
        session?.Dispose();
        try { _worker?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
        try { _refresher?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cancellation?.Dispose();
        _cancellation = null;
        _worker = null;
        _refresher = null;
    }

    public void Dispose() => Stop();

    public IReadOnlyList<UdpFlowRow> GetRows()
    {
        var now = Stopwatch.GetTimestamp();
        return _flows.Select(pair => pair.Value.Snapshot(pair.Key.Remote, now))
            .Where(row => row.Active)
            .OrderByDescending(row => row.Row.ReceivedPackets + row.Row.SentPackets)
            .Take(30)
            .Select(row => row.Row)
            .ToArray();
    }

    private static Dictionary<int, GameUdpEndpoint> BuildPortMap()
    {
        var map = new Dictionary<int, GameUdpEndpoint>();
        try
        {
            foreach (var endpoint in GameProcessService.GetUdpEndpoints())
                map[endpoint.LocalPort] = endpoint;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            // Keep the previous attribution data; the next refresh will try again.
        }
        return map;
    }

    private void OnPacket(IPAddress source, int sourcePort, IPAddress destination, int destinationPort, int size, int headerPid, bool outbound)
    {
        var owners = _portOwners;
        var sourceOwned = owners.ContainsKey(sourcePort);
        var destinationOwned = owners.ContainsKey(destinationPort);

        IPAddress remoteAddress;
        int remotePort;
        GameUdpEndpoint? owner = null;
        if (sourceOwned && !destinationOwned)
        {
            owner = owners[sourcePort];
            (remoteAddress, remotePort) = (destination, destinationPort);
        }
        else if (destinationOwned && !sourceOwned)
        {
            owner = owners[destinationPort];
            (remoteAddress, remotePort) = (source, sourcePort);
        }
        else
        {
            // Ownership unknown or ambiguous: fall back to the direction convention of the event.
            (remoteAddress, remotePort) = outbound ? (destination, destinationPort) : (source, sourcePort);
        }

        if (Array.IndexOf(NoisePorts, remotePort) >= 0 || IsLocalOrMulticast(remoteAddress))
            return;

        var pid = owner?.ProcessId ?? headerPid;
        var name = owner?.ProcessName ?? ResolveName(pid);
        if (_processFilter.Length > 0 && !name.Equals(_processFilter, StringComparison.OrdinalIgnoreCase))
            return;

        var flow = _flows.GetOrAdd((pid, $"{remoteAddress}:{remotePort}"), _ => new Flow(name));
        flow.Add(size, outbound, Stopwatch.GetTimestamp());
    }

    private static readonly ConcurrentDictionary<int, string> NameCache = new();

    private static string ResolveName(int pid) => NameCache.GetOrAdd(pid, id =>
    {
        try { return Process.GetProcessById(id).ProcessName; }
        catch (ArgumentException) { return $"PID {id}"; }
        catch (InvalidOperationException) { return $"PID {id}"; }
    });

    private static bool IsLocalOrMulticast(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
            return true;
        if (address.IsIPv6Multicast || address.IsIPv6LinkLocal)
            return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] >= 224;
    }

    private sealed record FlowSnapshot(UdpFlowRow Row, bool Active);

    private sealed class Flow(string processName)
    {
        private readonly object _gate = new();
        private readonly Queue<long> _receiveTimes = new();
        private long _sentPackets, _receivedPackets, _sentBytes, _receivedBytes, _lastReceive;

        public void Add(int size, bool outbound, long timestamp)
        {
            lock (_gate)
            {
                if (outbound)
                {
                    _sentPackets++;
                    _sentBytes += size;
                    return;
                }
                _receivedPackets++;
                _receivedBytes += size;
                _lastReceive = timestamp;
                _receiveTimes.Enqueue(timestamp);
                Prune(timestamp);
            }
        }

        private void Prune(long now)
        {
            var limit = now - WindowSeconds * Stopwatch.Frequency;
            while (_receiveTimes.Count > 0 && _receiveTimes.Peek() < limit)
                _receiveTimes.Dequeue();
        }

        public FlowSnapshot Snapshot(string remote, long now)
        {
            lock (_gate)
            {
                Prune(now);
                var times = _receiveTimes.ToArray();
                double maxGapMs = 0;
                for (var i = 1; i < times.Length; i++)
                    maxGapMs = Math.Max(maxGapMs, (times[i] - times[i - 1]) * 1000d / Stopwatch.Frequency);
                var sinceLastMs = _lastReceive == 0 ? 0 : (now - _lastReceive) * 1000d / Stopwatch.Frequency;
                if (times.Length > 0)
                    maxGapMs = Math.Max(maxGapMs, sinceLastMs);

                var sentActive = _sentPackets > 0 && _receivedPackets == 0;
                var active = _receivedPackets > 0 ? sinceLastMs < 30_000 : sentActive;
                var rate = times.Length / (double)WindowSeconds;
                var row = new UdpFlowRow(
                    processName, remote, _sentPackets, _receivedPackets,
                    FormatBytes(_sentBytes), FormatBytes(_receivedBytes),
                    $"{rate:0.#} пак/с",
                    times.Length == 0 ? "—" : $"{maxGapMs:0} мс",
                    _lastReceive == 0 ? "—" : $"{sinceLastMs / 1000d:0.0} с назад");
                return new FlowSnapshot(row, active);
            }
        }

        private static string FormatBytes(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} Б",
            < 1024 * 1024 => $"{bytes / 1024d:0.#} КБ",
            _ => $"{bytes / 1024d / 1024d:0.##} МБ"
        };
    }
}
