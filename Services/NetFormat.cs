namespace PingBoost.Services;

/// <summary>Number formatting for the connection table (speeds in bits per second, sizes in bytes).</summary>
public static class NetFormat
{
    /// <summary>Set from the interface language so dynamic cell text matches the static texts.</summary>
    public static bool English { get; set; }

    public static string Rate(double bytesPerSecond)
    {
        var bits = bytesPerSecond * 8;
        if (bits < 1)
            return "—";
        var (mega, kilo, unit) = English ? ("Mbit/s", "Kbit/s", "bit/s") : ("Мбит/с", "Кбит/с", "бит/с");
        if (bits >= 1_000_000)
            return $"{bits / 1_000_000:0.##} {mega}";
        if (bits >= 1_000)
            return $"{bits / 1_000:0} {kilo}";
        return $"{bits:0} {unit}";
    }

    public static string Bytes(long bytes)
    {
        if (bytes <= 0)
            return "—";
        var (b, kb, mb, gb) = English ? ("B", "KB", "MB", "GB") : ("Б", "КБ", "МБ", "ГБ");
        return bytes switch
        {
            < 1024 => $"{bytes} {b}",
            < 1024 * 1024 => $"{bytes / 1024d:0.#} {kb}",
            < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d:0.#} {mb}",
            _ => $"{bytes / 1024d / 1024d / 1024d:0.##} {gb}"
        };
    }
}
