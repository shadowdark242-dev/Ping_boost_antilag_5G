using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace PingBoost.Services;

public sealed record PowerShellResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

public static class PowerShellRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Quotes a value as a PowerShell single-quoted string literal.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static async Task<PowerShellResult> RunAsync(string script)
    {
        // Errors are reported as plain text on stderr; EncodedCommand would otherwise wrap them in CLIXML.
        var body = "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; "
                   + "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; "
                   + "try { " + script + " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(body));
        var info = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить powershell.exe.");
        using var timeout = new CancellationTokenSource(Timeout);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new PowerShellResult(-1, string.Empty, "Команда PowerShell не завершилась за 90 секунд и была остановлена.");
        }

        return new PowerShellResult(process.ExitCode, (await output).Trim(), (await error).Trim());
    }
}
