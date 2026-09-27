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
        using var directory = new TemporaryDirectory();
        var readyFile = Path.Combine(directory.Path, "term-handler-ready");
        using var process = new ProcessService(
            "/bin/sh",
            $"-c \"trap '' TERM; : > '{readyFile}'; exec sleep 30\"",
            directory.Path,
            false,
            false,
            null,
            null);
        try
        {
            await process.StartAsync();
            await WaitUntilAsync(() => File.Exists(readyFile) || !process.IsRunning, TimeSpan.FromSeconds(5));
            await File.Exists(readyFile).Should().BeTrue();

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
        finally
        {
            await process.StopAsync(graceful: false);
            await WaitUntilAsync(() => !process.IsRunning, TimeSpan.FromSeconds(5));
            await process.IsRunning.Should().BeFalse();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"v2rayn-graceful-stop-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
