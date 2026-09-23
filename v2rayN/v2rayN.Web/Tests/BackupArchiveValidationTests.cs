using System.IO.Compression;
using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class BackupArchiveValidationTests
{
    [Test]
    public async Task SafeServiceLibBackupIsAccepted()
    {
        var archivePath = CreateArchive(("guiConfigs/guiNConfig.json", "{}"));
        try
        {
            await V2rayRuntime.IsSafeBackupArchive(archivePath).Should().BeTrue();
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Test]
    public async Task ArchiveTraversalPathIsRejected()
    {
        var archivePath = CreateArchive(
            ("guiConfigs/guiNConfig.json", "{}"),
            ("guiConfigs/../../outside.txt", "not safe"));
        try
        {
            await V2rayRuntime.IsSafeBackupArchive(archivePath).Should().BeFalse();
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    private static string CreateArchive(params (string Name, string Content)[] files)
    {
        var path = Path.Combine(Path.GetTempPath(), $"v2rayn-web-test-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(file.Name).Open());
            writer.Write(file.Content);
        }
        return path;
    }
}
