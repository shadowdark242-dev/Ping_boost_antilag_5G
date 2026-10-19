using System.IO;
using System.Globalization;
using System.Text;

namespace PingBoost.Services;

public static class CsvReportService
{
    public static void Export(string path, IEnumerable<PingSample> samples)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("timestamp_local,target,latency_ms,status");
        foreach (var sample in samples)
        {
            var timestamp = sample.Timestamp.ToLocalTime().ToString("O", CultureInfo.InvariantCulture);
            var latency = sample.LatencyMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            writer.WriteLine($"{Escape(timestamp)},{Escape(sample.Host)},{Escape(latency)},{Escape(sample.Status)}");
        }
    }

    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
