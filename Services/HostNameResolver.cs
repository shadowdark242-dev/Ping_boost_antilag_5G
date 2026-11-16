using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PingBoost.Services;

/// <summary>
/// Finds a readable server name for an IP address. The names Windows itself resolved (DNS client cache)
/// are preferred because they are the names programs asked for (for example www.google.com);
/// otherwise a reverse DNS (PTR) lookup is done in the background and cached.
/// </summary>
public static class HostNameResolver
{
    private static readonly ConcurrentDictionary<IPAddress, string> DnsCache = new();
    private static readonly ConcurrentDictionary<IPAddress, (string? Name, DateTime At)> Reverse = new();
    private static readonly ConcurrentDictionary<IPAddress, byte> Pending = new();
    private static readonly SemaphoreSlim Concurrency = new(4);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);
    private static readonly char[] LineSeparators = ['\r', '\n'];

    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>Returns the known name or an empty string; unknown addresses are resolved in the background.</summary>
    public static string Lookup(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return "localhost";
        if (DnsCache.TryGetValue(address, out var name))
            return name;
        if (Reverse.TryGetValue(address, out var reverse))
        {
            if (reverse.Name is not null)
                return reverse.Name;
            if (DateTime.UtcNow - reverse.At > RetryAfter)
                QueueReverse(address);
            return string.Empty;
        }
        QueueReverse(address);
        return string.Empty;
    }

    /// <summary>Adds the A/AAAA records from the Windows DNS client cache; names seen once are kept.</summary>
    public static async Task RefreshDnsCacheAsync()
    {
        var result = await PowerShellRunner.RunAsync(
            "Get-DnsClientCache | ForEach-Object { [string]$_.Entry + '|' + [string]$_.Data }");
        if (!result.Success)
            return;
        foreach (var line in result.Output.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length != 2 || parts[0].Length == 0)
                continue;
            // CNAME rows carry a name instead of an address and are skipped by TryParse.
            if (IPAddress.TryParse(parts[1].Trim(), out var address))
                DnsCache.TryAdd(Normalize(address), parts[0].Trim().TrimEnd('.'));
        }
    }

    private static void QueueReverse(IPAddress address)
    {
        if (!Pending.TryAdd(address, 0))
            return;
        _ = Task.Run(async () =>
        {
            string? name = null;
            await Concurrency.WaitAsync();
            try
            {
                var entry = await Dns.GetHostEntryAsync(address).WaitAsync(TimeSpan.FromSeconds(4));
                if (!string.IsNullOrWhiteSpace(entry.HostName) && entry.HostName != address.ToString())
                    name = entry.HostName.TrimEnd('.');
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
            {
                // No PTR record or the lookup timed out: the address stays unnamed.
            }
            finally
            {
                Concurrency.Release();
                Reverse[address] = (name, DateTime.UtcNow);
                Pending.TryRemove(address, out _);
            }
        });
    }
}
