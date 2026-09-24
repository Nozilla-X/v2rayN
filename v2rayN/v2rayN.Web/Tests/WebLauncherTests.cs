using v2rayN.Web.Launcher;

namespace v2rayN.Web.Tests;

public class WebLauncherTests
{
    [Test]
    public async Task ForegroundFlagAlwaysSelectsForegroundMode()
    {
        var options = WebLaunchOptions.Parse(["--foreground"], isLinux: true, daemonEnvironment: false, containerEnvironment: false);
        await (options.Mode == WebLaunchMode.Foreground).Should().BeTrue();
    }

    [Test]
    public async Task BackgroundFlagSelectsLauncherMode()
    {
        var options = WebLaunchOptions.Parse(["--background"], isLinux: true, daemonEnvironment: true, containerEnvironment: true);
        await (options.Mode == WebLaunchMode.BackgroundLauncher).Should().BeTrue();
    }

    [Test]
    public async Task BackgroundChildRunsTheForegroundHostWithoutRecursiveLauncherArguments()
    {
        var options = WebLaunchOptions.Parse(["--background-child", "--no-open", "--urls", "http://127.0.0.1:5090"], isLinux: true, daemonEnvironment: false, containerEnvironment: false);
        await (options.Mode == WebLaunchMode.BackgroundChild).Should().BeTrue();
        await options.NoOpen.Should().BeTrue();
        await options.HostArguments.SequenceEqual(["--urls", "http://127.0.0.1:5090"]).Should().BeTrue();
    }

    [Test]
    public async Task SystemdAndContainerDefaultsRemainForeground()
    {
        var systemd = WebLaunchOptions.Parse([], isLinux: true, daemonEnvironment: true, containerEnvironment: false);
        var container = WebLaunchOptions.Parse([], isLinux: true, daemonEnvironment: false, containerEnvironment: true);

        await (systemd.Mode == WebLaunchMode.Foreground).Should().BeTrue();
        await (container.Mode == WebLaunchMode.Foreground).Should().BeTrue();
        await (WebLaunchOptions.Parse(["--foreground"], true, true, true).Mode == WebLaunchMode.Foreground).Should().BeTrue();
    }

    [Test]
    public async Task NativeLinuxDefaultUsesTheBackgroundLauncher()
    {
        var desktop = WebLaunchOptions.Parse([], isLinux: true, daemonEnvironment: false, containerEnvironment: false);
        var otherPlatform = WebLaunchOptions.Parse([], isLinux: false, daemonEnvironment: false, containerEnvironment: false);

        await (desktop.Mode == WebLaunchMode.BackgroundLauncher).Should().BeTrue();
        await (otherPlatform.Mode == WebLaunchMode.Foreground).Should().BeTrue();
    }

    [Test]
    public async Task SystemdEnvironmentDetectionRecognizesInvocationMarkers()
    {
        var environment = new Dictionary<string, string?> { ["INVOCATION_ID"] = "unit-run-id" };
        await LauncherEnvironment.IsDaemonEnvironment(environment).Should().BeTrue();
        await LauncherEnvironment.IsContainerEnvironment(new Dictionary<string, string?> { ["container"] = "podman" }).Should().BeTrue();
    }

    [Test]
    public async Task ExistingHealthyInstanceIsReusedAndBrowserAdapterIsInvoked()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var directory = new TemporaryDirectory();
        var lockPath = Path.Combine(directory.Path, "instance.lock");
        await WebInstanceLock.TryAcquire(lockPath, writeOwner: true, out var heldLock).Should().BeTrue();
        using (heldLock)
        {
            var browser = new FakeBrowserOpener();
            var result = await ExistingInstanceHandler.TryReuseAsync(
                lockPath,
                new Uri("http://127.0.0.1:5080/api/health"),
                new Uri("http://127.0.0.1:5080/"),
                new FakeHealthProbe(true, WebInstanceLock.ReadOwnerProcessId(lockPath)),
                browser,
                noOpen: false,
                TimeSpan.FromSeconds(1));

            await result.Existing.Should().BeTrue();
            await result.BrowserOpened.Should().BeTrue();
            await (browser.LastUri == new Uri("http://127.0.0.1:5080/")).Should().BeTrue();
        }
    }

    [Test]
    public async Task HealthyEndpointFromAnotherInstanceIsNotReusedForThisDataHome()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var directory = new TemporaryDirectory();
        var lockPath = Path.Combine(directory.Path, "instance.lock");
        await WebInstanceLock.TryAcquire(lockPath, writeOwner: true, out var heldLock).Should().BeTrue();
        using (heldLock)
        {
            var expectedOwner = WebInstanceLock.ReadOwnerProcessId(lockPath);
            var browser = new FakeBrowserOpener();
            var timedOut = false;
            try
            {
                await ExistingInstanceHandler.TryReuseAsync(
                    lockPath,
                    new Uri("http://127.0.0.1:5080/api/health"),
                    new Uri("http://127.0.0.1:5080/"),
                    new FakeHealthProbe(true, expectedOwner + 1),
                    browser,
                    noOpen: false,
                    TimeSpan.FromMilliseconds(50));
            }
            catch (TimeoutException)
            {
                timedOut = true;
            }

            await timedOut.Should().BeTrue();
            await (browser.LastUri is null).Should().BeTrue();
        }
    }

    [Test]
    public async Task UnlockedStaleLockFileCanBeRecovered()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var directory = new TemporaryDirectory();
        var lockPath = Path.Combine(directory.Path, "instance.lock");
        await File.WriteAllTextAsync(lockPath, "99999999\n");

        var result = await ExistingInstanceHandler.TryReuseAsync(
            lockPath,
            new Uri("http://127.0.0.1:5080/api/health"),
            new Uri("http://127.0.0.1:5080/"),
            new FakeHealthProbe(false),
            new FakeBrowserOpener(),
            noOpen: true,
            TimeSpan.FromMilliseconds(100));

        await result.Existing.Should().BeFalse();
        await WebInstanceLock.TryAcquire(lockPath, writeOwner: false, out var recovered).Should().BeTrue();
        recovered!.Dispose();
    }

    [Test]
    public async Task LockIsReleasedWhenOwnerExitsAndDataHomesUseDistinctLocks()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var directory = new TemporaryDirectory();
        var firstPath = Path.Combine(directory.Path, "one", "instance.lock");
        var secondPath = Path.Combine(directory.Path, "two", "instance.lock");
        await WebInstanceLock.TryAcquire(firstPath, writeOwner: true, out var first).Should().BeTrue();
        await WebInstanceLock.TryAcquire(secondPath, writeOwner: true, out var second).Should().BeTrue();
        using (first)
        using (second)
        {
            await WebInstanceLock.IsHeld(firstPath).Should().BeTrue();
            await WebInstanceLock.IsHeld(secondPath).Should().BeTrue();
        }
        await WebInstanceLock.IsHeld(firstPath).Should().BeFalse();
        await WebInstanceLock.TryAcquire(firstPath, writeOwner: false, out var recovered).Should().BeTrue();
        recovered!.Dispose();
    }

    [Test]
    public async Task LauncherMessagesUseTheSharedLocaleResources()
    {
        var chinese = LauncherMessages.Started("http://127.0.0.1:5080", LauncherLocale.SimplifiedChinese);
        var english = LauncherMessages.Started("http://127.0.0.1:5080", LauncherLocale.English);

        await chinese.Contains("管理页面", StringComparison.Ordinal).Should().BeTrue();
        await english.Contains("Web UI", StringComparison.Ordinal).Should().BeTrue();
        await (LauncherMessages.ResolveLocale("zh_Hant_TW") == LauncherLocale.TraditionalChinese).Should().BeTrue();
        await (LauncherMessages.ResolveLocale("zh_CN.UTF-8") == LauncherLocale.SimplifiedChinese).Should().BeTrue();
        await (LauncherMessages.ResolveLocale("fr_FR.UTF-8") == LauncherLocale.English).Should().BeTrue();
    }

    private sealed class FakeHealthProbe(bool healthy, int? processId = null) : IWebHealthProbe
    {
        public Task<WebHealthProbeResult> ProbeAsync(Uri healthUri, CancellationToken cancellationToken) =>
            Task.FromResult(new WebHealthProbeResult(healthy, processId));
    }

    private sealed class FakeBrowserOpener : IBrowserOpener
    {
        public Uri? LastUri { get; private set; }

        public bool TryOpen(Uri webUiUri)
        {
            LastUri = webUiUri;
            return true;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"v2rayn-web-launcher-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
