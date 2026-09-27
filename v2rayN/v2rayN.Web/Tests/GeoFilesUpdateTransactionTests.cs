using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class GeoFilesUpdateTransactionTests
{
    [Test]
    public async Task FailedGeoFilesUpdateRestoresReplacedFilesAndRemovesNewFiles()
    {
        using var directory = new TemporaryDirectory();
        var existing = Path.Combine(directory.Path, "geoip.dat");
        var created = Path.Combine(directory.Path, "geosite-custom.srs");
        await File.WriteAllTextAsync(existing, "old-geoip");

        var failed = false;
        try
        {
            await GeoFilesUpdateTransaction.ApplyAsync(
                () => Directory.GetFiles(directory.Path),
                [existing, created],
                async _ =>
                {
                    await File.WriteAllTextAsync(existing, "partially-updated-geoip");
                    await File.WriteAllTextAsync(created, "new-srs");
                    throw new IOException("simulated later download/apply failure");
                },
                CancellationToken.None);
        }
        catch (IOException exception) when (exception.Message.Contains("simulated later", StringComparison.Ordinal))
        {
            failed = true;
        }

        await failed.Should().BeTrue();
        await (await File.ReadAllTextAsync(existing)).Should().BeEqualTo("old-geoip");
        await File.Exists(created).Should().BeFalse();
    }

    [Test]
    public async Task MissingRequiredGeoFileFailsAndRestoresTheOriginalSet()
    {
        using var directory = new TemporaryDirectory();
        var existing = Path.Combine(directory.Path, "geosite.dat");
        var required = Path.Combine(directory.Path, "geoip.dat");
        await File.WriteAllTextAsync(existing, "old-geosite");
        await File.WriteAllTextAsync(required, "old-geoip");

        var failed = false;
        try
        {
            await GeoFilesUpdateTransaction.ApplyAsync(
                () => Directory.GetFiles(directory.Path),
                [existing, required],
                async _ =>
                {
                    await File.WriteAllTextAsync(existing, "new-geosite");
                    File.Delete(required);
                },
                CancellationToken.None);
        }
        catch (IOException exception) when (exception.Message.Contains("did not produce", StringComparison.Ordinal))
        {
            failed = true;
        }

        await failed.Should().BeTrue();
        await (await File.ReadAllTextAsync(existing)).Should().BeEqualTo("old-geosite");
        await (await File.ReadAllTextAsync(required)).Should().BeEqualTo("old-geoip");
    }

    [Test]
    public async Task VerifiedGeoFilesUpdateKeepsNewFiles()
    {
        using var directory = new TemporaryDirectory();
        var target = Path.Combine(directory.Path, "geosite.dat");

        await GeoFilesUpdateTransaction.ApplyAsync(
            () => Directory.GetFiles(directory.Path),
            [target],
            token => File.WriteAllTextAsync(target, "verified-update", token),
            CancellationToken.None);

        await (await File.ReadAllTextAsync(target)).Should().BeEqualTo("verified-update");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"v2rayn-web-geofiles-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
