namespace v2rayN.Web.Services;

/// <summary>
/// Adds Web-owned backup/verification/rollback around ServiceLib's public GeoFiles updater.
/// The updater remains responsible for deciding which files to download and how to download them.
/// </summary>
internal static class GeoFilesUpdateTransaction
{
    public static async Task ApplyAsync(
        Func<IReadOnlyCollection<string>> enumerateManagedFiles,
        IEnumerable<string> requiredFiles,
        Func<CancellationToken, Task> update,
        CancellationToken cancellationToken)
    {
        var required = requiredFiles
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        var originalTargets = enumerateManagedFiles()
            .Concat(required)
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        var backupRoot = Path.Combine(Path.GetTempPath(), $"v2rayn-web-geofiles-{Guid.NewGuid():N}");
        var backups = new Dictionary<string, GeoFileBackup>(PathComparer);
        var transactionCompleted = false;
        var rollbackCompleted = false;
        var updateStarted = false;

        try
        {
            Directory.CreateDirectory(backupRoot);
            for (var index = 0; index < originalTargets.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = originalTargets[index];
                if (!File.Exists(target)) continue;
                var backup = Path.Combine(backupRoot, index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
                var lastWriteTimeUtc = File.GetLastWriteTimeUtc(target);
                File.Copy(target, backup);
                backups.Add(target, new GeoFileBackup(backup, lastWriteTimeUtc));
            }

            try
            {
                updateStarted = true;
                await update(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var requiredFile in required)
                {
                    if (!File.Exists(requiredFile) || new FileInfo(requiredFile).Length <= 0)
                    {
                        throw new IOException($"GeoFiles update did not produce a non-empty {Path.GetFileName(requiredFile)}.");
                    }
                }
                transactionCompleted = true;
            }
            catch (Exception updateException)
            {
                try
                {
                    var originallyExisting = backups.Keys.ToHashSet(PathComparer);
                    foreach (var target in enumerateManagedFiles()
                                 .Concat(required)
                                 .Select(Path.GetFullPath)
                                 .Distinct(PathComparer))
                    {
                        if (!originallyExisting.Contains(target) && File.Exists(target))
                        {
                            File.Delete(target);
                        }
                    }

                    foreach (var (target, backup) in backups)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(backup.BackupPath, target, overwrite: true);
                        File.SetLastWriteTimeUtc(target, backup.LastWriteTimeUtc);
                    }
                    rollbackCompleted = true;
                }
                catch (Exception rollbackException)
                {
                    throw new IOException(
                        $"GeoFiles update failed and rollback is incomplete; the backup set was retained at {backupRoot}.",
                        new AggregateException(updateException, rollbackException));
                }

                throw;
            }
        }
        finally
        {
            if ((transactionCompleted || rollbackCompleted || !updateStarted) && Directory.Exists(backupRoot))
            {
                try { Directory.Delete(backupRoot, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A cleanup failure does not invalidate a successful update or rollback.
                }
            }
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record GeoFileBackup(string BackupPath, DateTime LastWriteTimeUtc);
}
