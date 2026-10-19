using System.Text.Json;
using System.IO;

namespace PingBoost.Services;

public sealed class AppSettings
{
    public string Language { get; set; } = "ru";
    public bool StartWithWindows { get; set; }
    /// <summary>Last game process name (without .exe), reused by the Optimization and UDP monitor tabs.</summary>
    public string GameProcessName { get; set; } = string.Empty;
    /// <summary>Addresses ticked in the test target list; empty means "use the defaults".</summary>
    public List<string> TestTargets { get; set; } = [];
    /// <summary>Addresses the user added to the target list by hand.</summary>
    public List<string> CustomTargets { get; set; } = [];
}

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PingBoost", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Use defaults if settings are missing or malformed; do not prevent app startup.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
