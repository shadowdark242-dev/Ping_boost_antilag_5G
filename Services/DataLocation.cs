using System.IO;

namespace PingBoost.Services;

/// <summary>
/// Settings and the restore backup live next to PingBoost.exe, so the app is portable.
/// If that folder is not writable (for example Program Files) the per-user folder is used instead.
/// Files left in the old per-user location by earlier builds are copied over once.
/// </summary>
public static class DataLocation
{
    private static readonly Lazy<string> Directory_ = new(ChooseDirectory);

    private static string LegacyDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PingBoost");

    public static string Resolve(string fileName)
    {
        var path = System.IO.Path.Combine(Directory_.Value, fileName);
        var legacy = System.IO.Path.Combine(LegacyDirectory, fileName);
        try
        {
            if (!File.Exists(path) && File.Exists(legacy) && !string.Equals(path, legacy, StringComparison.OrdinalIgnoreCase))
                File.Copy(legacy, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Migration is best effort; the app starts with defaults otherwise.
        }
        return path;
    }

    private static string ChooseDirectory()
    {
        // ProcessPath is the real exe location; AppContext.BaseDirectory can point to the single-file extraction folder.
        var exeDirectory = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDirectory) && IsWritable(exeDirectory))
            return exeDirectory;
        return LegacyDirectory;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = System.IO.Path.Combine(directory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
