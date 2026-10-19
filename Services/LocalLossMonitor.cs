using System.Net.NetworkInformation;

namespace PingBoost.Services;

/// <summary>System-wide counters since monitoring started, plus a description of what grew since the last poll.</summary>
public sealed record LocalLossReport(
    long UdpDiscarded,
    long UdpErrors,
    long NicInDiscarded,
    long NicInErrors,
    long NicOutDiscarded,
    long NicOutErrors,
    string? NewEvents)
{
    public long Total => UdpDiscarded + UdpErrors + NicInDiscarded + NicInErrors + NicOutDiscarded + NicOutErrors;
}

/// <summary>
/// Tells local loss (this PC dropped packets: socket buffers, adapter, driver) from loss outside the PC.
/// The counters are system-wide, not per process, so small sporadic values from other programs are normal.
/// </summary>
public sealed class LocalLossMonitor
{
    private readonly record struct Counters(
        long UdpDiscarded, long UdpErrors, long NicInDiscarded, long NicInErrors, long NicOutDiscarded, long NicOutErrors)
    {
        public static Counters operator -(Counters left, Counters right) => new(
            left.UdpDiscarded - right.UdpDiscarded, left.UdpErrors - right.UdpErrors,
            left.NicInDiscarded - right.NicInDiscarded, left.NicInErrors - right.NicInErrors,
            left.NicOutDiscarded - right.NicOutDiscarded, left.NicOutErrors - right.NicOutErrors);
    }

    private Counters _baseline;
    private Counters _previous;

    public void Start()
    {
        _baseline = _previous = Read();
    }

    public LocalLossReport Poll()
    {
        var now = Read();
        var growth = now - _previous;
        _previous = now;
        var total = now - _baseline;

        var events = new List<string>();
        if (growth.UdpDiscarded > 0)
            events.Add($"UDP-датаграмм отброшено системой: +{growth.UdpDiscarded}");
        if (growth.UdpErrors > 0)
            events.Add($"UDP-датаграмм с ошибками: +{growth.UdpErrors}");
        if (growth.NicInDiscarded > 0)
            events.Add($"адаптер отбросил входящих: +{growth.NicInDiscarded}");
        if (growth.NicInErrors > 0)
            events.Add($"адаптер: входящих с ошибками +{growth.NicInErrors}");
        if (growth.NicOutDiscarded > 0)
            events.Add($"адаптер отбросил исходящих: +{growth.NicOutDiscarded}");
        if (growth.NicOutErrors > 0)
            events.Add($"адаптер: исходящих с ошибками +{growth.NicOutErrors}");

        return new LocalLossReport(
            total.UdpDiscarded, total.UdpErrors, total.NicInDiscarded, total.NicInErrors,
            total.NicOutDiscarded, total.NicOutErrors,
            events.Count == 0 ? null : string.Join("; ", events));
    }

    private static Counters Read()
    {
        long udpDiscarded = 0, udpErrors = 0;
        try
        {
            var udp = IPGlobalProperties.GetIPGlobalProperties().GetUdpIPv4Statistics();
            udpDiscarded = udp.IncomingDatagramsDiscarded;
            udpErrors = udp.IncomingDatagramsWithErrors;
        }
        catch (NetworkInformationException)
        {
            // Leave the UDP counters at zero if Windows cannot provide them.
        }

        long inDiscarded = 0, inErrors = 0, outDiscarded = 0, outErrors = 0;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            try
            {
                var statistics = adapter.GetIPStatistics();
                inDiscarded += statistics.IncomingPacketsDiscarded;
                inErrors += statistics.IncomingPacketsWithErrors;
                outDiscarded += statistics.OutgoingPacketsDiscarded;
                outErrors += statistics.OutgoingPacketsWithErrors;
            }
            catch (NetworkInformationException)
            {
                // An adapter that disappears between enumeration and reading is simply skipped.
            }
        }
        return new Counters(udpDiscarded, udpErrors, inDiscarded, inErrors, outDiscarded, outErrors);
    }
}
