using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingBoost.Services;

public sealed record PingSample(string Host, DateTimeOffset Timestamp, long? LatencyMilliseconds, string Status)
{
    public bool IsSuccess => LatencyMilliseconds.HasValue;
}

public sealed class NetworkMonitor
{
    public IReadOnlyList<string> GetActiveAdapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(adapter =>
            {
                var gateway = adapter.GetIPProperties().GatewayAddresses
                    .Select(entry => entry.Address)
                    .FirstOrDefault(address => !address.Equals(System.Net.IPAddress.Any)
                                               && !address.Equals(System.Net.IPAddress.IPv6Any));
                var gatewayText = gateway is null ? "шлюз не определён" : $"шлюз {gateway}";
                var speedText = adapter.Speed > 0 ? $" · {FormatSpeed(adapter.Speed)}" : string.Empty;
                return $"{adapter.Name} ({adapter.NetworkInterfaceType}) · {gatewayText}{speedText}";
            })
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<PingSample> MeasureAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 1200).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new PingSample(host, DateTimeOffset.Now, reply.RoundtripTime, reply.Status.ToString())
                : new PingSample(host, DateTimeOffset.Now, null, reply.Status.ToString());
        }
        catch (PingException ex)
        {
            return new PingSample(host, DateTimeOffset.Now, null, ex.InnerException?.Message ?? ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new PingSample(host, DateTimeOffset.Now, null, ex.Message);
        }
        catch (SocketException ex)
        {
            return new PingSample(host, DateTimeOffset.Now, null, ex.Message);
        }
    }

    private static string FormatSpeed(long bitsPerSecond)
    {
        var megabits = bitsPerSecond / 1_000_000d;
        return megabits >= 1000
            ? $"{megabits / 1000:0.#} Гбит/с"
            : $"{megabits:0.#} Мбит/с";
    }
}
