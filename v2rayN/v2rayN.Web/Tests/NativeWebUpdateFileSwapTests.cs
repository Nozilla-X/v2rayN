using v2rayN.Web.Launcher;

namespace v2rayN.Web.Tests;

public class NativeWebUpdateFileSwapTests
{
    [Test]
    public async Task SuccessfulAppSwapPreservesCoreAndConfigDirectories()
    {
        using var directory = new TemporaryDirectory();
        var plan = await CreatePlanAsync(directory.Path, includeNewIdentity: true);
        NativeWebUpdateHelper.CopyCurrentAppToBackup(plan);
        NativeWebUpdateHelper.SwapCandidateAppIntoPlace(plan);

        await (await File.ReadAllTextAsync(Path.Combine(plan.InstallDirectory, "v2rayN.Web"))).Should().BeEqualTo("new-web");
        await (await File.ReadAllTextAsync(Path.Combine(plan.InstallDirectory, "wwwroot", "index.html"))).Should().BeEqualTo("new-ui");
        await (await File.ReadAllTextAsync(Path.Combine(plan.InstallDirectory, "bin", "xray", "xray"))).Should().BeEqualTo("updated-core");
        await (await File.ReadAllTextAsync(Path.Combine(plan.InstallDirectory, "guiConfigs", "guiNConfig.json"))).Should().BeEqualTo("user-config");
    }

    [Test]
    public async Task PartialFileReplacementFailureRestoresOldAppWithoutTouchingCoreFiles()
    {
        using var directory = new TemporaryDirectory();
        var plan = await CreatePlanAsync(directory.Path, includeNewIdentity: false);
        NativeWebUpdateHelper.CopyCurrentAppToBackup(plan);

        var rollback = await NativeWebUpdateWorkflow.ApplyAsync(
            () => { NativeWebUpdateHelper.SwapCandidateAppIntoPlace(plan); return Task.CompletedTask; },
            () => Task.FromResult(false),
            () => { NativeWebUpdateHelper.RestorePreviousApp(plan); return Task.CompletedTask; },
            () => Task.FromResult(true),
            _ => { },
            () => Task.CompletedTask);

        await rollback.Success.Should().BeFalse();
        await rollback.RollbackSucceeded.Should().BeTrue();
        await (await File.ReadAllTextAsync(Path.Combine(plan.InstallDirectory, "v2rayN.Web"))).Should().BeEqualTo("old-web");
        await (await File.ReadAllTextAsync(Path.Combine(plan.InstallDirectory, "wwwroot", "index.html"))).Should().BeEqualTo("old-ui");
        await (await File.ReadAllTextAsync(Path.Combine(plan.InstallDirectory, "bin", "xray", "xray"))).Should().BeEqualTo("updated-core");
    }

    private static async Task<NativeWebUpdatePlan> CreatePlanAsync(string root, bool includeNewIdentity)
    {
        var install = Path.Combine(root, "native");
        var parent = Path.GetDirectoryName(install)!;
        var candidate = Path.Combine(parent, ".v2rayn-web-candidate-test");
        var backup = Path.Combine(parent, ".v2rayn-web-backup-test");
        Directory.CreateDirectory(Path.Combine(install, "wwwroot"));
        Directory.CreateDirectory(Path.Combine(install, "bin", "xray"));
        Directory.CreateDirectory(Path.Combine(install, "guiConfigs"));
        Directory.CreateDirectory(Path.Combine(candidate, "wwwroot"));
        await File.WriteAllTextAsync(Path.Combine(install, "v2rayN.Web"), "old-web");
        await File.WriteAllTextAsync(Path.Combine(install, "v2rayN.Web.build.json"), "old-identity");
        await File.WriteAllTextAsync(Path.Combine(install, "wwwroot", "index.html"), "old-ui");
        await File.WriteAllTextAsync(Path.Combine(install, "bin", "xray", "xray"), "updated-core");
        await File.WriteAllTextAsync(Path.Combine(install, "guiConfigs", "guiNConfig.json"), "user-config");
        await File.WriteAllTextAsync(Path.Combine(candidate, "v2rayN.Web"), "new-web");
        await File.WriteAllTextAsync(Path.Combine(candidate, "wwwroot", "index.html"), "new-ui");
        if (includeNewIdentity)
            await File.WriteAllTextAsync(Path.Combine(candidate, "v2rayN.Web.build.json"), "new-identity");
        return new NativeWebUpdatePlan(
            install,
            candidate,
            backup,
            Path.Combine(root, "instance.lock"),
            "http://127.0.0.1:5080/api/health",
            [],
            "7.25.2-web.2",
            "new-commit",
            "linux-x64",
            "7.25.2-web.1",
            Path.Combine(root, "runtime-intent.json"),
            Path.Combine(root, "update-progress.json"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"v2rayn-web-swap-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
