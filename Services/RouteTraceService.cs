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
            var reply = await ping.SendPingAsync(host, ProbeTimeoutMilliseconds, payload, options);
            cancellationToken.ThrowIfCancellationRequested();

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
                    ? reply.RoundtripTime
                    : null,
                status));

            if (reached)
                return true;

            if (reply.Status is not (IPStatus.TtlExpired or IPStatus.TimedOut))
                return false;
        }

        return false;
    }
}
