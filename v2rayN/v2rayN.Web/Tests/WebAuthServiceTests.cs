using System.Net;
using System.Text.Json;
using v2rayN.Web.Security;

namespace v2rayN.Web.Tests;

public class WebAuthServiceTests
{
    private const string ManagementKey = "test-management-key-2026";

    [Test]
    public async Task MissingEnvironmentAndPersistedKeyRequiresSetup()
    {
        using var directory = new TemporaryDirectory();
        var auth = new WebAuthService(Path.Combine(directory.Path, "guiConfigs", "web-auth.json"), null);

        await auth.SetupRequired.Should().BeTrue();
        await auth.EnvironmentKeyConfigured.Should().BeFalse();
        await auth.ValidateKey(ManagementKey).Should().BeFalse();
    }

    [Test]
    public async Task EnvironmentKeyTakesPrecedenceWithoutPersistingIt()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "guiConfigs", "web-auth.json");
        var auth = new WebAuthService(path, ManagementKey);

        await auth.SetupRequired.Should().BeFalse();
        await auth.EnvironmentKeyConfigured.Should().BeTrue();
        await auth.ValidateKey(ManagementKey).Should().BeTrue();
        await auth.ValidateKey("a-different-management-key").Should().BeFalse();
        await File.Exists(path).Should().BeFalse();
    }

    [Test]
    public async Task SetupPersistsOnlySaltAndVerifierAndVerifiesTheKeyImmediately()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "guiConfigs", "web-auth.json");
        var auth = new WebAuthService(path, null);

        var result = await auth.SetupAsync(ManagementKey, ManagementKey, IPAddress.Loopback);

        await (result == WebSetupResult.Created).Should().BeTrue();
        await auth.SetupRequired.Should().BeFalse();
        await auth.ValidateKey(ManagementKey).Should().BeTrue();
        await auth.ValidateKey("not-the-management-key").Should().BeFalse();

        var persisted = await File.ReadAllTextAsync(path);
        await persisted.Contains(ManagementKey, StringComparison.Ordinal).Should().BeFalse();
        using var document = JsonDocument.Parse(persisted);
        await (document.RootElement.GetProperty("version").GetInt32() == 1).Should().BeTrue();
        await (!string.IsNullOrWhiteSpace(document.RootElement.GetProperty("salt").GetString())).Should().BeTrue();
        await (!string.IsNullOrWhiteSpace(document.RootElement.GetProperty("keyVerifier").GetString())).Should().BeTrue();
        if (OperatingSystem.IsLinux())
        {
            await (File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite)).Should().BeTrue();
        }
    }

    [Test]
    public async Task SetupRejectsShortOrMismatchedKeys()
    {
        using var directory = new TemporaryDirectory();
        var auth = new WebAuthService(Path.Combine(directory.Path, "web-auth.json"), null);

        await ((await auth.SetupAsync("short", "short", IPAddress.Loopback)) == WebSetupResult.KeyTooShort).Should().BeTrue();
        await ((await auth.SetupAsync(ManagementKey, "a-different-key", IPAddress.Loopback)) == WebSetupResult.KeysDoNotMatch).Should().BeTrue();
        await auth.SetupRequired.Should().BeTrue();
    }

    [Test]
    public async Task SetupCanOnlyBeCompletedOnce()
    {
        using var directory = new TemporaryDirectory();
        var auth = new WebAuthService(Path.Combine(directory.Path, "web-auth.json"), null);

        await ((await auth.SetupAsync(ManagementKey, ManagementKey, IPAddress.Loopback)) == WebSetupResult.Created).Should().BeTrue();
        await ((await auth.SetupAsync("a-second-management-key", "a-second-management-key", IPAddress.Loopback)) == WebSetupResult.AlreadyConfigured).Should().BeTrue();
        await auth.ValidateKey(ManagementKey).Should().BeTrue();
        await auth.ValidateKey("a-second-management-key").Should().BeFalse();
    }

    [Test]
    public async Task SetupRejectsNonLoopbackRemoteAddressEvenWhenForwardingHeadersAreNotRelevant()
    {
        using var directory = new TemporaryDirectory();
        var auth = new WebAuthService(Path.Combine(directory.Path, "web-auth.json"), null);
        var remoteAddress = IPAddress.Parse("192.0.2.20");

        await WebAuthService.IsLoopbackAddress(remoteAddress).Should().BeFalse();
        await ((await auth.SetupAsync(ManagementKey, ManagementKey, remoteAddress)) == WebSetupResult.Forbidden).Should().BeTrue();
        await auth.SetupRequired.Should().BeTrue();
    }

    [Test]
    public async Task IPv4LoopbackIsAllowedForSetup()
    {
        using var directory = new TemporaryDirectory();
        var auth = new WebAuthService(Path.Combine(directory.Path, "web-auth.json"), null);

        await WebAuthService.IsLoopbackAddress(IPAddress.Loopback).Should().BeTrue();
        await ((await auth.SetupAsync(ManagementKey, ManagementKey, IPAddress.Loopback)) == WebSetupResult.Created).Should().BeTrue();
    }

    [Test]
    public async Task IPv6LoopbackIsAllowedForSetup()
    {
        using var directory = new TemporaryDirectory();
        var auth = new WebAuthService(Path.Combine(directory.Path, "web-auth.json"), null);

        await WebAuthService.IsLoopbackAddress(IPAddress.IPv6Loopback).Should().BeTrue();
        await ((await auth.SetupAsync(ManagementKey, ManagementKey, IPAddress.IPv6Loopback)) == WebSetupResult.Created).Should().BeTrue();
    }

    [Test]
    public async Task ForwardedClientAddressHeadersCannotGrantSetupAccess()
    {
        using var directory = new TemporaryDirectory();
        var auth = new WebAuthService(Path.Combine(directory.Path, "web-auth.json"), null);

        var result = await auth.SetupAsync(
            ManagementKey,
            ManagementKey,
            IPAddress.Loopback,
            forwardedAddressHeaderPresent: true);

        await (result == WebSetupResult.Forbidden).Should().BeTrue();
        await auth.SetupRequired.Should().BeTrue();
    }

    [Test]
    public async Task PersistedVerifierSurvivesServiceRecreation()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "guiConfigs", "web-auth.json");
        var firstInstance = new WebAuthService(path, null);
        await ((await firstInstance.SetupAsync(ManagementKey, ManagementKey, IPAddress.Loopback)) == WebSetupResult.Created).Should().BeTrue();

        var restartedInstance = new WebAuthService(path, null);
        await restartedInstance.SetupRequired.Should().BeFalse();
        await restartedInstance.ValidateKey(ManagementKey).Should().BeTrue();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"v2rayn-web-auth-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
