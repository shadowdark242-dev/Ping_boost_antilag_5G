using System.Net.NetworkInformation;

namespace PingBoost.Services;

public sealed record MtuProbeResult(int? Mtu, string Message);

/// <summary>
/// Finds the largest IPv4 packet that passes to a host without fragmentation (same idea as <c>ping -f -l</c>).
/// </summary>
public static class MtuProbeService
{
    private const int HeaderBytes = 28; // 20 bytes IPv4 + 8 bytes ICMP
    private const int MinPayload = 548;
    private const int MaxPayload = 1472;

    public static async Task<MtuProbeResult> ProbeAsync(string host, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        progress?.Report("Проверяю, что узел отвечает на обычный ICMP…");
        if (!await FitsAsync(ping, host, 32, cancellationToken))
            return new MtuProbeResult(null, "Узел не отвечает на ICMP. Выберите другой адрес, например 1.1.1.1 или адрес игрового сервера.");

        if (await FitsAsync(ping, host, MaxPayload, cancellationToken))
            return new MtuProbeResult(MaxPayload + HeaderBytes, $"Пакет {MaxPayload + HeaderBytes} байт проходит без фрагментации: стандартный MTU 1500 подходит.");

        if (!await FitsAsync(ping, host, MinPayload, cancellationToken))
            return new MtuProbeResult(null, "Даже пакет 576 байт не проходит без фрагментации. Вероятно, узел или оператор блокирует ICMP; результат ненадёжен.");

        int low = MinPayload, high = MaxPayload;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            progress?.Report($"Проверяю размер {middle + HeaderBytes} байт…");
            if (await FitsAsync(ping, host, middle, cancellationToken))
                low = middle;
            else
                high = middle;
        }

        var mtu = low + HeaderBytes;
        return new MtuProbeResult(mtu, $"Максимальный пакет без фрагментации до {host}: {mtu} байт.");
    }

    private static async Task<bool> FitsAsync(Ping ping, string host, int payload, CancellationToken cancellationToken)
    {
        var buffer = new byte[payload];
        var options = new PingOptions(64, dontFragment: true);
        // Mobile links lose single probes, so a size is rejected only after two failed attempts.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(host, 1500, buffer, options);
                if (reply.Status == IPStatus.Success)
                    return true;
                if (reply.Status == IPStatus.PacketTooBig)
                    return false;
            }
            catch (PingException)
            {
                return false;
            }
        }
        return false;
    }
}
