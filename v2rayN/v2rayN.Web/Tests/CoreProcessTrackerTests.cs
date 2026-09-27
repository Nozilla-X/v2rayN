using System.Diagnostics;
using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class CoreProcessTrackerTests
{
    [Test]
    public async Task WebTrackerCapturesNewCoreProcessesAndPrunesExitedIdentities()
    {
        if (!OperatingSystem.IsLinux()) return;

        var existingProcessIds = CoreProcessTracker.CaptureExistingProcessIds();
        using var process = StartSleepProcess();
        try
        {
            var tracked = CoreProcessTracker.CaptureStartedProcesses(existingProcessIds, ["sleep"]);
            await tracked.Any(identity => identity.ProcessId == process.Id).Should().BeTrue();
            await CoreProcessTracker.GetActiveProcesses(tracked)
                .Any(identity => identity.ProcessId == process.Id)
                .Should().BeTrue();

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await CoreProcessTracker.GetActiveProcesses(tracked).Length.Should().BeEqualTo(0);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Test]
    public async Task WebTrackerDoesNotClaimProcessesThatPredateItsLaunchSnapshot()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var process = StartSleepProcess();
        var existingProcessIds = CoreProcessTracker.CaptureExistingProcessIds();
        var tracked = CoreProcessTracker.CaptureStartedProcesses(existingProcessIds, ["sleep"]);

        try
        {
            await tracked.Any(identity => identity.ProcessId == process.Id).Should().BeFalse();
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    [Test]
    public async Task WebTrackerRecognizesAnExecutableRunningThroughItsInterpreter()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = new TemporaryDirectory();
        var executable = Path.Combine(directory.Path, "test-core-shim");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nsleep 60\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var existingProcessIds = CoreProcessTracker.CaptureExistingProcessIds();
        using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })
            ?? throw new InvalidOperationException("Could not start the Core shim test process.");
        try
        {
            var tracked = CoreProcessTracker.CaptureStartedProcesses(existingProcessIds, ["test-core-shim"]);
            await tracked.Any(identity => identity.ProcessId == process.Id).Should().BeTrue();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static Process StartSleepProcess()
    {
        var startInfo = new ProcessStartInfo("/bin/sleep") { UseShellExecute = false };
        startInfo.ArgumentList.Add("60");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the test process.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"v2rayn-web-core-tracker-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
