using System.Text.Json;
using v2rayN.Web.Contracts;
using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class RestoreRuntimeRecoveryTests
{
    [Test]
    public async Task RestoreMarkerWithStoppedCoreSuppressesAutostart()
    {
        var started = new List<string>();
        var result = await RestoreRuntimeRecovery.RecoverAsync(
            new RestoreRuntimeIntent(false, "old"),
            "restored",
            "default",
            (_, _) => Task.FromResult(true),
            (id, _) =>
            {
                started.Add(id);
                return Task.FromResult(OperationView.Ok("core.started"));
            },
            _ => { },
            CancellationToken.None);

        await (result.Source == RestoreRuntimeRecoverySource.NotRequested).Should().BeTrue();
        await (started.Count == 0).Should().BeTrue();
        await RestoreRuntimeRecovery.ShouldAutoStart(new RestoreRuntimeIntent(false, null), configured: true).Should().BeFalse();
        await RestoreRuntimeRecovery.ShouldAutoStart(null, configured: true).Should().BeTrue();
    }

    [Test]
    public async Task RunningCoreRestoresThePreviousProfileWhenItStillExists()
    {
        var (result, started, _) = await RecoverAsync(
            new RestoreRuntimeIntent(true, "old"),
            "selected",
            "default",
            ["old", "selected", "default"]);

        await (result.Source == RestoreRuntimeRecoverySource.PreferredProfile).Should().BeTrue();
        await (result.ProfileId == "old").Should().BeTrue();
        await started.Contains("old").Should().BeTrue();
        await started.Count.Should().BeEqualTo(1);
    }

    [Test]
    public async Task MissingPreviousProfileFallsBackToRestoredSelectedProfile()
    {
        var (result, started, logs) = await RecoverAsync(
            new RestoreRuntimeIntent(true, "old"),
            "selected",
            "default",
            ["selected", "default"]);

        await (result.Source == RestoreRuntimeRecoverySource.RestoredSelection).Should().BeTrue();
        await (result.ProfileId == "selected").Should().BeTrue();
        await started.Contains("selected").Should().BeTrue();
        await logs.Any(log => log.Contains("Previous profile old no longer exists", StringComparison.Ordinal)).Should().BeTrue();
        await logs.Any(log => log.Contains("Falling back to restored selected profile selected", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Test]
    public async Task MissingPreviousAndSelectedProfilesFallBackToServiceLibDefault()
    {
        var (result, started, logs) = await RecoverAsync(
            new RestoreRuntimeIntent(true, "old"),
            "missing-selected",
            "default",
            ["default"]);

        await (result.Source == RestoreRuntimeRecoverySource.DefaultProfile).Should().BeTrue();
        await (result.ProfileId == "default").Should().BeTrue();
        await started.Contains("default").Should().BeTrue();
        await logs.Any(log => log.Contains("Falling back to the ServiceLib default profile default", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Test]
    public async Task RestoreWithNoAvailableProfileKeepsCoreStoppedAndLogsContext()
    {
        var (result, started, logs) = await RecoverAsync(
            new RestoreRuntimeIntent(true, "old"),
            "missing-selected",
            null,
            []);

        await (result.Source == RestoreRuntimeRecoverySource.NoProfile).Should().BeTrue();
        await (started.Count == 0).Should().BeTrue();
        await logs.Any(log => log.Contains("no valid profile exists in the restored configuration", StringComparison.Ordinal)).Should().BeTrue();
        await logs.Any(log => log.Contains("Core remains stopped", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Test]
    public async Task FailedFallbackPreflightIsNotReportedAsSuccessOrRetried()
    {
        var starts = new List<string>();
        var logs = new List<string>();
        var result = await RestoreRuntimeRecovery.RecoverAsync(
            new RestoreRuntimeIntent(true, "removed"),
            "also-removed",
            "fallback",
            (id, _) => Task.FromResult(id == "fallback"),
            (id, _) =>
            {
                starts.Add(id);
                return Task.FromResult(OperationView.Fail("profile_validation_failed", "errors.invalidInput"));
            },
            logs.Add,
            CancellationToken.None);

        await (result.Source == RestoreRuntimeRecoverySource.StartFailed).Should().BeTrue();
        await (result.FailureCode == "profile_validation_failed").Should().BeTrue();
        await starts.Contains("fallback").Should().BeTrue();
        await starts.Count.Should().BeEqualTo(1);
        await logs.Any(log => log.Contains("No further restart will be attempted", StringComparison.Ordinal)).Should().BeTrue();
        await (!logs.Any(log => log.Contains("Core restored successfully", StringComparison.Ordinal))).Should().BeTrue();
    }

    [Test]
    public async Task RestoreRuntimeMarkerIsClaimedAndConsumedExactlyOnce()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "restore-state.json");
        await File.WriteAllTextAsync(path + ".consuming-abandoned", "stale");
        var markerJson = JsonSerializer.Serialize(new RestoreRuntimeIntent(true, "old"));
        await File.WriteAllTextAsync(path, markerJson);

        var consumed = await V2rayRuntime.ConsumeRestoreRuntimeStateAsync(path, CancellationToken.None);
        var secondRead = await V2rayRuntime.ConsumeRestoreRuntimeStateAsync(path, CancellationToken.None);

        await (consumed is { WasRunning: true, PreferredProfileId: "old" }).Should().BeTrue();
        await (secondRead is null).Should().BeTrue();
        await File.Exists(path).Should().BeFalse();
        await (Directory.GetFiles(directory.Path, "*.consuming-*").Length == 0).Should().BeTrue();
        await markerJson.Should().Contain("PreferredProfileId");
        await (!markerJson.Contains("ProcessIds", StringComparison.Ordinal)
            && !markerJson.Contains("ApiPort", StringComparison.Ordinal)
            && !markerJson.Contains("Listeners", StringComparison.Ordinal)).Should().BeTrue();
    }

    private static async Task<(RestoreRuntimeRecoveryResult Result, List<string> Started, List<string> Logs)> RecoverAsync(
        RestoreRuntimeIntent intent,
        string? selected,
        string? defaultProfile,
        HashSet<string> existing)
    {
        var started = new List<string>();
        var logs = new List<string>();
        var result = await RestoreRuntimeRecovery.RecoverAsync(
            intent,
            selected,
            defaultProfile,
            (id, _) => Task.FromResult(existing.Contains(id)),
            (id, _) =>
            {
                started.Add(id);
                return Task.FromResult(OperationView.Ok("core.started"));
            },
            logs.Add,
            CancellationToken.None);
        return (result, started, logs);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"v2rayn-web-restore-intent-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
