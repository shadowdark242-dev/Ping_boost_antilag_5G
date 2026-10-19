using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace PingBoost.Services;

/// <summary>Raises the CPU priority class of a running game; the previous class is restored on request.</summary>
public sealed class GameProcessPriorityService
{
    private readonly Dictionary<int, ProcessPriorityClass> _original = [];

    public int Apply(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName.Trim());
        if (name.Length == 0)
            return 0;
        var changed = 0;
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (process.PriorityClass == ProcessPriorityClass.High)
                        continue;
                    _original.TryAdd(process.Id, process.PriorityClass);
                    process.PriorityClass = ProcessPriorityClass.High;
                    changed++;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // The process exited or is protected (anti-cheat); leave it untouched.
                }
            }
        }
        return changed;
    }

    public void Restore()
    {
        foreach (var (pid, priority) in _original)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.PriorityClass = priority;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            {
                // Process already closed: its priority is gone with it.
            }
        }
        _original.Clear();
    }
}
