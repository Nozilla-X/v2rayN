using ServiceLib.Services;

namespace v2rayN.Web.Tests;

public class CoreProcessShutdownTests
{
    [Test]
    public async Task GracefulStopWaitsForTheCoreChildToExit()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var process = new ProcessService("/bin/sleep", "60", "/", false, false, null, null);
        await process.StartAsync();

        await process.StopAsync(graceful: true);

        await process.IsRunning.Should().BeFalse();
    }

    [Test]
    public async Task GracefulStopTimeoutDoesNotForceKillTheCoreChild()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var process = new ProcessService("/bin/sh", "-c \"trap '' TERM; exec sleep 30\"", "/", false, false, null, null);
        await process.StartAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var canceled = false;
        try
        {
            await process.StopAsync(graceful: true, cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        await canceled.Should().BeTrue();
        await process.IsRunning.Should().BeTrue();
    }
}
