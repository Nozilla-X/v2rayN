using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class WebUpdatePackageStagerTests
{
    [Test]
    public async Task OfficialVersionChannelHandlesOlderNewerEqualPrereleaseAndLegacyBuilds()
    {
        await WebUpdatePackageStager.IsUpdateAvailable("7.25.2", "7.25.3", allowPrerelease: false)
            .Should().BeTrue();
        await WebUpdatePackageStager.IsUpdateAvailable("7.25.3", "7.25.3", allowPrerelease: true)
            .Should().BeFalse();
        await WebUpdatePackageStager.IsUpdateAvailable("7.25.3", "7.25.3", allowPrerelease: true,
            currentCommit: "old-build", candidateCommit: "release-build").Should().BeTrue();
        await WebUpdatePackageStager.IsUpdateAvailable("7.25.3", "7.25.3", allowPrerelease: true,
            currentCommit: "same", candidateCommit: "same").Should().BeFalse();
        await WebUpdatePackageStager.IsUpdateAvailable("7.25.3", "7.25.2", allowPrerelease: true)
            .Should().BeFalse();
        await WebUpdatePackageStager.IsUpdateAvailable("0.0.0-dev", "7.25.3", allowPrerelease: false)
            .Should().BeTrue();
        // Development identities are older than the official releases they track.
        await WebUpdatePackageStager.IsUpdateAvailable("7.25.3-dev.2", "7.25.3", allowPrerelease: true)
            .Should().BeTrue();
        // Migration from the retired web-v/-web.N channel: a legacy build upgrades to the official tag,
        // and an official build is never "updated" back to a legacy prerelease.
        await WebUpdatePackageStager.IsUpdateAvailable("7.25.2-web.5", "7.25.3", allowPrerelease: false)
            .Should().BeTrue();
        await WebUpdatePackageStager.IsUpdateAvailable("7.25.3", "7.25.3-web.1", allowPrerelease: true)
            .Should().BeFalse();
        await WebUpdatePackageStager.IsUpdateAvailable("not-a-version", "7.25.3", allowPrerelease: true)
            .Should().BeFalse();
        await WebUpdatePackageStager.ShouldConsiderRelease(isPrerelease: false, allowPrerelease: false).Should().BeTrue();
        await WebUpdatePackageStager.ShouldConsiderRelease(isPrerelease: true, allowPrerelease: false).Should().BeFalse();
        await WebUpdatePackageStager.ShouldConsiderRelease(isPrerelease: true, allowPrerelease: true).Should().BeTrue();

        var build = WebBuildIdentity.Current;
        await WebUpdatePackageStager.IsValidVersion(build.Version).Should().BeTrue();
        await WebReleaseChannel.IsValidRepository(build.Repository).Should().BeTrue();
        await string.IsNullOrWhiteSpace(build.Rid).Should().BeFalse();
    }

    [Test]
    public async Task ManifestRequiresWebProductHttpsAssetsAndAnExactRid()
    {
        var manifest = new WebUpdateManifest("v2rayN.Web", "7.25.3", "0123456789abcdef", "2026-09-28T00:00:00Z",
        [
            new WebUpdatePackage("linux-x64", "v2rayN.Web-app-linux-64.tar.gz",
                "https://github.com/2dust/v2rayN/releases/download/7.25.3/v2rayN.Web-app-linux-64.tar.gz",
                new string('a', 64), 1234),
            new WebUpdatePackage("linux-arm64", "v2rayN.Web-app-linux-arm64.tar.gz",
                "https://github.com/2dust/v2rayN/releases/download/7.25.3/v2rayN.Web-app-linux-arm64.tar.gz",
                new string('b', 64), 2345),
        ]);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var parsed = WebUpdatePackageStager.ParseManifest(json);
        await parsed.Version.Should().BeEqualTo("7.25.3");
        await WebUpdatePackageStager.RequirePackage(parsed, "linux-x64").Rid.Should().BeEqualTo("linux-x64");
        await WebUpdatePackageStager.RequirePackage(parsed, "linux-arm64").Rid.Should().BeEqualTo("linux-arm64");
        await WebReleaseChannel.IsTrustedAssetUrl(parsed.Packages[0].Url, "2dust/v2rayN", parsed.Version, parsed.Packages[0].Asset)
            .Should().BeTrue();

        var wrongRidRejected = false;
        try { _ = WebUpdatePackageStager.RequirePackage(parsed, "linux-riscv64"); }
        catch (InvalidDataException) { wrongRidRejected = true; }
        await wrongRidRejected.Should().BeTrue();

        var oversized = manifest with
        {
            Packages = [manifest.Packages[0] with { Size = WebUpdatePackageStager.MaximumArchiveBytes + 1 }],
        };
        var oversizedRejected = false;
        try
        {
            _ = WebUpdatePackageStager.ParseManifest(JsonSerializer.Serialize(oversized, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        catch (InvalidDataException) { oversizedRejected = true; }
        await oversizedRejected.Should().BeTrue();
    }

    [Test]
    public async Task AppOnlyTarPackageIsVerifiedAndExtractedForItsExactIdentity()
    {
        using var directory = new TemporaryDirectory();
        var archive = Path.Combine(directory.Path, "web-app.tar.gz");
        var identity = new WebUpdatePackageIdentity("v2rayN.Web", "7.25.3", "0123456789abcdef", "2026-09-28T01:00:00Z", "linux-x64");
        await WriteArchiveAsync(archive,
        [
            ("v2rayN.Web", Encoding.UTF8.GetBytes("native executable")),
            ("wwwroot/index.html", Encoding.UTF8.GetBytes("<html>Web</html>")),
            ("v2rayN.Web.build.json", JsonSerializer.SerializeToUtf8Bytes(identity, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
        ]);
        var manifest = CreateManifest(archive, identity.Version, identity.Commit, "linux-x64", identity.BuildDate);
        var package = manifest.Packages[0];

        var extracted = Path.Combine(directory.Path, "stage");
        var actualIdentity = await WebUpdatePackageStager.VerifyAndExtractAsync(
            archive, extracted, manifest, package, CancellationToken.None);
        await actualIdentity.Should().BeEqualTo(identity);
        await File.Exists(Path.Combine(extracted, "bin", "xray", "xray")).Should().BeFalse();
        await File.Exists(Path.Combine(extracted, "guiConfigs", "guiNConfig.json")).Should().BeFalse();
        await File.Exists(Path.Combine(extracted, "webData", "web-auth.json")).Should().BeFalse();
    }

    [Test]
    public async Task ChecksumMismatchCorruptArchiveWrongRidAndMissingExecutableAreRejected()
    {
        using var directory = new TemporaryDirectory();
        var identity = new WebUpdatePackageIdentity("v2rayN.Web", "7.25.4", "abcdef0123456789", "2026-09-28T02:00:00Z", "linux-x64");
        var archive = Path.Combine(directory.Path, "good.tar.gz");
        await WriteArchiveAsync(archive,
        [
            ("v2rayN.Web", Encoding.UTF8.GetBytes("native executable")),
            ("wwwroot/index.html", Encoding.UTF8.GetBytes("index")),
            ("v2rayN.Web.build.json", JsonSerializer.SerializeToUtf8Bytes(identity, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
        ]);
        var manifest = CreateManifest(archive, identity.Version, identity.Commit, "linux-x64", identity.BuildDate);
        var damaged = Path.Combine(directory.Path, "damaged.tar.gz");
        var bytes = await File.ReadAllBytesAsync(archive);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(damaged, bytes);
        await ExpectInvalidDataAsync(() => WebUpdatePackageStager.VerifyAndExtractAsync(
            damaged, Path.Combine(directory.Path, "checksum-failure"), manifest, manifest.Packages[0], CancellationToken.None));

        await ExpectInvalidDataAsync(() => WebUpdatePackageStager.VerifyAndExtractAsync(
            archive, Path.Combine(directory.Path, "wrong-rid"), manifest,
            manifest.Packages[0] with { Rid = "linux-arm64" }, CancellationToken.None));

        var missingExecutableArchive = Path.Combine(directory.Path, "missing.tar.gz");
        await WriteArchiveAsync(missingExecutableArchive,
        [
            ("wwwroot/index.html", Encoding.UTF8.GetBytes("index")),
            ("v2rayN.Web.build.json", JsonSerializer.SerializeToUtf8Bytes(identity, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
        ]);
        var missingManifest = CreateManifest(missingExecutableArchive, identity.Version, identity.Commit, "linux-x64", identity.BuildDate);
        await ExpectInvalidDataAsync(() => WebUpdatePackageStager.VerifyAndExtractAsync(
            missingExecutableArchive, Path.Combine(directory.Path, "missing-executable"), missingManifest,
            missingManifest.Packages[0], CancellationToken.None));
    }

    [Test]
    public async Task TarTraversalAndSymbolicLinksAreRejected()
    {
        using var directory = new TemporaryDirectory();
        var identity = new WebUpdatePackageIdentity("v2rayN.Web", "7.25.5", "0123abcdef", "2026-09-28T03:00:00Z", "linux-x64");
        foreach (var entryName in new[] { "../escape", "/absolute", "wwwroot/../escape" })
        {
            var archive = Path.Combine(directory.Path, Guid.NewGuid().ToString("N") + ".tar.gz");
            await WriteArchiveAsync(archive, [(entryName, Encoding.UTF8.GetBytes("nope"))]);
            var manifest = CreateManifest(archive, identity.Version, identity.Commit, "linux-x64", identity.BuildDate);
            await ExpectInvalidDataAsync(() => WebUpdatePackageStager.VerifyAndExtractAsync(
                archive, Path.Combine(directory.Path, Guid.NewGuid().ToString("N")), manifest,
                manifest.Packages[0], CancellationToken.None));
        }

        var symlinkArchive = Path.Combine(directory.Path, "symlink.tar.gz");
        await using (var file = File.Create(symlinkArchive))
        await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            var link = new PaxTarEntry(TarEntryType.SymbolicLink, "wwwroot/index.html") { LinkName = "../../outside" };
            tar.WriteEntry(link);
        }
        var symlinkManifest = CreateManifest(symlinkArchive, identity.Version, identity.Commit, "linux-x64", identity.BuildDate);
        await ExpectInvalidDataAsync(() => WebUpdatePackageStager.VerifyAndExtractAsync(
            symlinkArchive, Path.Combine(directory.Path, "symlink-stage"), symlinkManifest,
            symlinkManifest.Packages[0], CancellationToken.None));

        var zipArchive = Path.Combine(directory.Path, "zip-traversal.zip");
        using (var zip = ZipFile.Open(zipArchive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../../outside");
            await using var content = entry.Open();
            await content.WriteAsync(Encoding.UTF8.GetBytes("not a supported Web package"));
        }
        var zipManifest = CreateManifest(zipArchive, identity.Version, identity.Commit, "linux-x64", identity.BuildDate);
        await ExpectInvalidDataAsync(() => WebUpdatePackageStager.VerifyAndExtractAsync(
            zipArchive, Path.Combine(directory.Path, "zip-stage"), zipManifest, zipManifest.Packages[0], CancellationToken.None));
    }

    private static WebUpdateManifest CreateManifest(string archive, string version, string commit, string rid, string buildDate)
    {
        using var stream = File.OpenRead(archive);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var asset = WebUpdatePackageStager.AppOnlyAssetName(rid)
            ?? throw new InvalidOperationException($"No app-only asset for {rid}.");
        return new WebUpdateManifest("v2rayN.Web", version, commit, buildDate,
        [new WebUpdatePackage(rid, asset,
            $"https://github.com/2dust/v2rayN/releases/download/{version}/{asset}",
            hash, new FileInfo(archive).Length)]);
    }

    private static async Task WriteArchiveAsync(string path, IReadOnlyList<(string Name, byte[] Content)> files)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: true);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var item in files)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, item.Name)
            {
                DataStream = new MemoryStream(item.Content),
            };
            tar.WriteEntry(entry);
        }
    }

    private static async Task ExpectInvalidDataAsync(Func<Task<WebUpdatePackageIdentity>> action)
    {
        var rejected = false;
        try { _ = await action(); }
        catch (InvalidDataException) { rejected = true; }
        await rejected.Should().BeTrue();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"v2rayn-web-package-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
