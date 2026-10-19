using System.Text.Json;
using System.IO;

namespace PingBoost.Services;

public sealed class AppSettings
{
    public string Language { get; set; } = "ru";
    public bool StartWithWindows { get; set; }
    public string SelectedProfile { get; set; } = string.Empty;
    public List<GameProfile> Profiles { get; set; } = [];
}

public sealed class GameProfile
{
    public string Name { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string MonitorTargets { get; set; } = "1.1.1.1\n8.8.8.8";
    public string SessionGateway { get; set; } = "";
    public string SessionRemote { get; set; } = "1.1.1.1";
    public string IdleTarget { get; set; } = "1.1.1.1";
    public int IdleSeconds { get; set; } = 15;
    public string KeepAliveUrl { get; set; } = "https://www.google.com/generate_204";
    public int KeepAliveSeconds { get; set; } = 10;
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
