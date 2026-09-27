using System.ComponentModel;
using System.Diagnostics;

namespace v2rayN.Web.Services;

internal sealed record CoreProcessIdentity(int ProcessId, string ProcessName, long? StartTimeUtcTicks);

/// <summary>
/// Tracks Core processes started by this Web host without depending on ServiceLib internals.
/// CoreManager remains responsible for launching/stopping; Web snapshots process identities
/// around its public LoadCore call and validates those identities during runtime transitions.
/// </summary>
internal static class CoreProcessTracker
{
    public static HashSet<int> CaptureExistingProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { ids.Add(process.Id); }
                catch (InvalidOperationException) { }
            }
        }
        return ids;
    }

    public static CoreProcessIdentity[] CaptureStartedProcesses(
        IReadOnlySet<int> existingProcessIds,
        IEnumerable<string> expectedExecutableNames)
    {
        var expected = expectedExecutableNames
            .Select(NormalizeProcessName)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expected.Count == 0) return [];

        var processes = new List<CoreProcessIdentity>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (existingProcessIds.Contains(process.Id) || process.HasExited) continue;
                    var name = NormalizeProcessName(process.ProcessName);
                    if (!expected.Contains(name) && !LinuxCommandLineReferencesCore(process.Id, expected)) continue;
                    long? startTimeTicks = null;
                    try { startTimeTicks = process.StartTime.ToUniversalTime().Ticks; }
                    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException) { }
                    processes.Add(new CoreProcessIdentity(process.Id, name, startTimeTicks));
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // A process may exit or become inaccessible while the process table is sampled.
                }
            }
        }

        return processes
            .DistinctBy(process => process.ProcessId)
            .OrderBy(process => process.ProcessId)
            .ToArray();
    }

    public static CoreProcessIdentity[] GetActiveProcesses(IEnumerable<CoreProcessIdentity> identities)
    {
        var active = new List<CoreProcessIdentity>();
        foreach (var identity in identities.DistinctBy(process => process.ProcessId))
        {
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                if (process.HasExited
                    || !string.Equals(NormalizeProcessName(process.ProcessName), identity.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (identity.StartTimeUtcTicks is long expectedStartTime)
                {
                    try
                    {
                        if (process.StartTime.ToUniversalTime().Ticks != expectedStartTime) continue;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                        continue;
                    }
                }
                active.Add(identity);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // A missing or inaccessible PID is not reported as an active Web-owned process.
            }
        }
        return active.OrderBy(process => process.ProcessId).ToArray();
    }

    private static string NormalizeProcessName(string name) =>
        Path.GetFileNameWithoutExtension(name.Trim());

    private static bool LinuxCommandLineReferencesCore(int processId, HashSet<string> expectedNames)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var commandLine = File.ReadAllText($"/proc/{processId}/cmdline");
            return commandLine.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Any(argument => expectedNames.Contains(NormalizeProcessName(argument)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
