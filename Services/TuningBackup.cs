using System.IO;
using System.Text.Json;

namespace PingBoost.Services;

public sealed class RegistryBackupEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public string Kind { get; set; } = "DWord";
    public string Value { get; set; } = "";
}

public sealed class AdapterBackup
{
    public Dictionary<string, string> Advanced { get; set; } = [];
    public string? DeviceSleep { get; set; }
    public string? Lso { get; set; }
    public string? Rsc { get; set; }
    public int? Mtu { get; set; }
}

/// <summary>
/// Values that existed before PingBoost changed them. Only the first observed value of each setting is kept,
/// so repeated "apply" runs never overwrite the original state.
/// </summary>
public sealed class TuningBackup
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public Dictionary<string, RegistryBackupEntry> Registry { get; set; } = [];
    public Dictionary<string, string> Tcp { get; set; } = [];
    public string? GlobalRsc { get; set; }
    public Dictionary<string, AdapterBackup> Adapters { get; set; } = [];
    public List<string> QosPolicies { get; set; } = [];
    public string? PowerPlanGuid { get; set; }
    public List<string> PausedServices { get; set; } = [];
}

public sealed class TuningBackupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PingBoost", "tuning-backup.json");

    public bool Exists => File.Exists(Path);

    public TuningBackup Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<TuningBackup>(File.ReadAllText(Path)) ?? new TuningBackup();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged backup must not block the app; a new one is created on the next change.
        }
        return new TuningBackup();
    }

    public void Save(TuningBackup backup)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(backup, JsonOptions));
    }

    public void Delete()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }
}
