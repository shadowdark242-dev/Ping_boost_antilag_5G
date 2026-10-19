using System.Diagnostics;
using System.Net.NetworkInformation;

namespace PingBoost.Services;

public sealed record RouteHopResult(int Hop, string Address, long? LatencyMilliseconds, string Status);

public sealed class RouteTraceService
{
    private const int MaximumHops = 24;
    private const int ProbeTimeoutMilliseconds = 900;

    public async Task<bool> TraceAsync(
        string host,
        IProgress<RouteHopResult> progress,
        CancellationToken cancellationToken)
    {
        var payload = new byte[32];

        for (var ttl = 1; ttl <= MaximumHops; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var ping = new Ping();
            var options = new PingOptions(ttl, dontFragment: true);
            var (reply, firstMilliseconds) = await ProbeAsync(ping, host, payload, options);
            cancellationToken.ThrowIfCancellationRequested();

            // .NET reports RoundtripTime = 0 for TTL-expired replies, so time is measured with a stopwatch.
            // Hops that answer get two more probes and the median is shown; silent hops are probed once.
            var latencies = new List<long> { firstMilliseconds };
            if (reply.Status is IPStatus.Success or IPStatus.TtlExpired)
            {
                for (var extra = 0; extra < 2; extra++)
                {
                    var (next, milliseconds) = await ProbeAsync(ping, host, payload, options);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (next.Status == reply.Status)
                        latencies.Add(milliseconds);
                }
            }
            var median = latencies.Order().ElementAt(latencies.Count / 2);

            var reached = reply.Status == IPStatus.Success;
            var address = reply.Address?.ToString() ?? "*";
            var status = reply.Status switch
            {
                IPStatus.Success => "Цель достигнута",
                IPStatus.TtlExpired => "Промежуточный узел ответил",
                IPStatus.TimedOut => "Нет ICMP-ответа (таймаут)",
                _ => reply.Status.ToString()
            };

            progress.Report(new RouteHopResult(
                ttl,
                address,
                reply.Status == IPStatus.Success || reply.Status == IPStatus.TtlExpired
                    ? median
                    : null,
                status));

            if (reached)
                return true;

            if (reply.Status is not (IPStatus.TtlExpired or IPStatus.TimedOut))
                return false;
        }

        return false;
    }

    private static async Task<(PingReply Reply, long Milliseconds)> ProbeAsync(
        Ping ping, string host, byte[] payload, PingOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        var reply = await ping.SendPingAsync(host, ProbeTimeoutMilliseconds, payload, options);
        stopwatch.Stop();
        return (reply, (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds));
    }
}
