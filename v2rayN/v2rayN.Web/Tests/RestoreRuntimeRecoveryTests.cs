using System.Text.Json;
using v2rayN.Web.Contracts;
using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class RuntimeRestartRecoveryTests
{
    [Test]
    public async Task RestoreMarkerWithStoppedCoreSuppressesAutostart()
    {
        var started = new List<string>();
        var result = await RuntimeRestartRecovery.RecoverAsync(
            new RuntimeRestartIntent(false, "old"),
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

        await (result.Source == RuntimeRestartRecoverySource.NotRequested).Should().BeTrue();
        await (started.Count == 0).Should().BeTrue();
        await RuntimeRestartRecovery.ShouldAutoStart(new RuntimeRestartIntent(false, null), configured: true).Should().BeFalse();
        await RuntimeRestartRecovery.ShouldAutoStart(null, configured: true).Should().BeTrue();
    }

    [Test]
    public async Task RunningCoreRestoresThePreviousProfileWhenItStillExists()
    {
        var (result, started, _) = await RecoverAsync(
            new RuntimeRestartIntent(true, "old"),
            "selected",
            "default",
            ["old", "selected", "default"]);

        await (result.Source == RuntimeRestartRecoverySource.PreferredProfile).Should().BeTrue();
        await (result.ProfileId == "old").Should().BeTrue();
        await started.Contains("old").Should().BeTrue();
        await started.Count.Should().BeEqualTo(1);
    }

    [Test]
    public async Task MissingPreviousProfileFallsBackToRestoredSelectedProfile()
    {
        var (result, started, logs) = await RecoverAsync(
            new RuntimeRestartIntent(true, "old"),
            "selected",
            "default",
            ["selected", "default"]);

        await (result.Source == RuntimeRestartRecoverySource.SelectedProfile).Should().BeTrue();
        await (result.ProfileId == "selected").Should().BeTrue();
        await started.Contains("selected").Should().BeTrue();
        await logs.Any(log => log.Contains("Previous profile old no longer exists", StringComparison.Ordinal)).Should().BeTrue();
        await logs.Any(log => log.Contains("Falling back to selected profile selected", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Test]
    public async Task MissingPreviousAndSelectedProfilesFallBackToServiceLibDefault()
    {
        var (result, started, logs) = await RecoverAsync(
            new RuntimeRestartIntent(true, "old"),
            "missing-selected",
            "default",
            ["default"]);

        await (result.Source == RuntimeRestartRecoverySource.DefaultProfile).Should().BeTrue();
        await (result.ProfileId == "default").Should().BeTrue();
        await started.Contains("default").Should().BeTrue();
        await logs.Any(log => log.Contains("Falling back to the ServiceLib default profile default", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Test]
    public async Task RestoreWithNoAvailableProfileKeepsCoreStoppedAndLogsContext()
    {
        var (result, started, logs) = await RecoverAsync(
            new RuntimeRestartIntent(true, "old"),
            "missing-selected",
            null,
            []);

        await (result.Source == RuntimeRestartRecoverySource.NoProfile).Should().BeTrue();
        await (started.Count == 0).Should().BeTrue();
        await logs.Any(log => log.Contains("no valid profile exists in the current configuration", StringComparison.Ordinal)).Should().BeTrue();
        await logs.Any(log => log.Contains("Core remains stopped", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Test]
    public async Task FailedFallbackPreflightIsNotReportedAsSuccessOrRetried()
    {
        var starts = new List<string>();
        var logs = new List<string>();
        var result = await RuntimeRestartRecovery.RecoverAsync(
            new RuntimeRestartIntent(true, "removed"),
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

        await (result.Source == RuntimeRestartRecoverySource.StartFailed).Should().BeTrue();
        await (result.FailureCode == "profile_validation_failed").Should().BeTrue();
        await starts.Contains("fallback").Should().BeTrue();
        await starts.Count.Should().BeEqualTo(1);
        await logs.Any(log => log.Contains("No further restart will be attempted", StringComparison.Ordinal)).Should().BeTrue();
        await (!logs.Any(log => log.Contains("Core restored successfully", StringComparison.Ordinal))).Should().BeTrue();
    }

    [Test]
    public async Task RestoreRuntimeMarkerSurvivesFailedInitializationAndIsConsumedAfterRecovery()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "restore-state.json");
        await File.WriteAllTextAsync(path + ".consuming-abandoned", JsonSerializer.Serialize(new RuntimeRestartIntent(false, null)));
        var markerJson = JsonSerializer.Serialize(new RuntimeRestartIntent(true, "old"));
        await File.WriteAllTextAsync(path + ".consuming-prior", markerJson);

        // Simulate ServiceLib initialization throwing before recovery reaches a terminal state.
        var firstStartupFailed = false;
        try
        {
            _ = await V2rayRuntime.LoadRuntimeRestartIntentAsync(path, CancellationToken.None);
            throw new InvalidOperationException("simulated initialization failure");
        }
        catch (InvalidOperationException)
        {
            firstStartupFailed = true;
        }

        var secondStartup = await V2rayRuntime.LoadRuntimeRestartIntentAsync(path, CancellationToken.None);
        await firstStartupFailed.Should().BeTrue();
        await (secondStartup is { WasRunning: true, PreferredProfileId: "old" }).Should().BeTrue();
        await File.Exists(path).Should().BeTrue();

        var stoppedIntentPath = Path.Combine(directory.Path, "restore-stopped.json");
        var stoppedIntent = new RuntimeRestartIntent(false, null);
        await File.WriteAllTextAsync(stoppedIntentPath, JsonSerializer.Serialize(stoppedIntent));
        try
        {
            _ = await V2rayRuntime.LoadRuntimeRestartIntentAsync(stoppedIntentPath, CancellationToken.None);
            throw new InvalidOperationException("simulated stopped-intent initialization failure");
        }
        catch (InvalidOperationException) { }
        var stoppedOnSecondStartup = await V2rayRuntime.LoadRuntimeRestartIntentAsync(stoppedIntentPath, CancellationToken.None);
        await (stoppedOnSecondStartup is { WasRunning: false }).Should().BeTrue();
        await File.Exists(stoppedIntentPath).Should().BeTrue();

        await V2rayRuntime.CommitRestoreRuntimeStateAsync(path);
        await V2rayRuntime.CommitRestoreRuntimeStateAsync(stoppedIntentPath);
        await File.Exists(path).Should().BeFalse();
        await File.Exists(stoppedIntentPath).Should().BeFalse();
        await (Directory.GetFiles(directory.Path, "*.consuming-*").Length == 0).Should().BeTrue();
        await markerJson.Should().Contain("PreferredProfileId");
        await (!markerJson.Contains("ProcessIds", StringComparison.Ordinal)
            && !markerJson.Contains("ApiPort", StringComparison.Ordinal)
            && !markerJson.Contains("Listeners", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Test]
    public async Task WebUpdateRuntimeIntentRemainsUntilTheExternalHealthVerifierCommitsIt()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "web-update-runtime-state.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new RuntimeRestartIntent(true, "removed")));
        var intent = await V2rayRuntime.LoadRuntimeRestartIntentAsync(path, CancellationToken.None);
        var started = new List<string>();
        var recovery = await RuntimeRestartRecovery.RecoverAsync(
            intent!,
            selectedProfileId: "restored",
            defaultProfileId: "default",
            profileExists: (id, _) => Task.FromResult(id is "restored" or "default"),
            startCore: (id, _) =>
            {
                started.Add(id);
                return Task.FromResult(OperationView.Ok("core.started"));
            },
            _ => { },
            CancellationToken.None);

        await (recovery.Source == RuntimeRestartRecoverySource.SelectedProfile).Should().BeTrue();
        await started.SequenceEqual(["restored"]).Should().BeTrue();
        await File.Exists(path).Should().BeTrue();
        await V2rayRuntime.DeleteRuntimeIntentAsync(path);
        await File.Exists(path).Should().BeFalse();
    }

    private static async Task<(RuntimeRestartRecoveryResult Result, List<string> Started, List<string> Logs)> RecoverAsync(
        RuntimeRestartIntent intent,
        string? selected,
        string? defaultProfile,
        HashSet<string> existing)
    {
        var started = new List<string>();
        var logs = new List<string>();
        var result = await RuntimeRestartRecovery.RecoverAsync(
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
