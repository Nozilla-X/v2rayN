using System.IO.Compression;
using System.Text.Json;
using SQLite;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Handler;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;
using v2rayN.Web.Contracts;

namespace v2rayN.Web.Services;

public sealed partial class V2rayRuntime
{
    private readonly object _restartGate = new();
    private Task? _restartTask;
    private const long MaxBackupArchiveBytes = 64L * 1024 * 1024;
    private const long MaxBackupExpandedBytes = 256L * 1024 * 1024;
    private const int MaxBackupEntries = 2048;
    internal const string WebAuthFileName = "web-auth.json";

    public WebDavSettingsView GetWebDavSettings() => new(
        Config.WebDavItem.Url,
        Config.WebDavItem.UserName,
        Config.WebDavItem.DirName,
        !string.IsNullOrEmpty(Config.WebDavItem.Password));

    public async Task<OperationView> UpdateWebDavSettingsAsync(WebDavSettingsInput input)
    {
        await _mutations.RunAsync(async () =>
        {
            Config.WebDavItem.Url = input.Url?.Trim();
            Config.WebDavItem.UserName = input.UserName?.Trim();
            if (input.Password is not null)
            {
                Config.WebDavItem.Password = input.Password;
            }
            Config.WebDavItem.DirName = input.DirName?.Trim();
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        });
        return OperationView.Ok(ApiMessageKeys.WebDavSettingsSaved);
    }

    public async Task<OperationView> CheckWebDavAsync()
    {
        var success = await WebDavManager.Instance.CheckConnection();
        if (!success)
        {
            AddLog("backup", WebDavManager.Instance.GetLastError());
        }
        return success
            ? OperationView.Ok(ApiMessageKeys.WebDavCheckSucceeded)
            : OperationView.Fail("webdav_check_failed", ApiMessageKeys.WebDavCheckFailed);
    }

    public async Task<(OperationView Result, string? FilePath)> CreateBackupArchiveAsync()
    {
        await _mutations.RunAsync(async () =>
        {
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
            await ProfileExManager.Instance.SaveTo();
            await StatisticsManager.Instance.SaveTo();
        });

        var archivePath = Utils.GetBackupPath($"backup_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.zip");
        var tempRoot = Utils.GetTempPath($"backup_{Utils.GetGuid(false)}");
        var tempConfigPath = Path.Combine(tempRoot, "guiConfigs");
        try
        {
            CopyConfigForBackup(Utils.GetConfigPath(), tempConfigPath);
            if (!FileUtils.CreateFromDirectory(tempRoot, archivePath))
            {
                return (OperationView.Fail("backup_create_failed", ApiMessageKeys.BackupArchiveInvalid), null);
            }
            if (new FileInfo(archivePath).Length > MaxBackupArchiveBytes)
            {
                File.Delete(archivePath);
                return (OperationView.Fail("backup_archive_too_large", ApiMessageKeys.BackupArchiveInvalid), null);
            }
            return (OperationView.Ok(ApiMessageKeys.BackupCreated, new { fileName = Path.GetFileName(archivePath) }), archivePath);
        }
        catch
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
            return (OperationView.Fail("backup_create_failed", ApiMessageKeys.BackupArchiveInvalid), null);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    public async Task<OperationView> BackupToWebDavAsync()
    {
        var (result, archivePath) = await CreateBackupArchiveAsync();
        if (!result.Success || archivePath is null)
        {
            return result;
        }

        try
        {
            if (!await WebDavManager.Instance.PutFile(archivePath))
            {
                AddLog("backup", WebDavManager.Instance.GetLastError());
                return OperationView.Fail("webdav_backup_failed", ApiMessageKeys.WebDavCheckFailed);
            }
            return OperationView.Ok(ApiMessageKeys.WebDavBackupSucceeded, new { fileName = Path.GetFileName(archivePath) });
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
    }

    public async Task<OperationView> RestoreFromWebDavAsync(CancellationToken cancellationToken)
    {
        var archivePath = Utils.GetTempPath($"restore_{Utils.GetGuid(false)}.zip");
        if (!await WebDavManager.Instance.GetRawFile(archivePath))
        {
            AddLog("backup", WebDavManager.Instance.GetLastError());
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
            return OperationView.Fail("webdav_restore_download_failed", ApiMessageKeys.WebDavRestoreFailed);
        }
        return await RestoreBackupArchiveAsync(archivePath, cancellationToken);
    }

    public async Task<OperationView> RestoreFromUploadAsync(Stream archive, CancellationToken cancellationToken)
    {
        var archivePath = Utils.GetTempPath($"restore_{Utils.GetGuid(false)}.zip");
        try
        {
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await CopyWithLimitAsync(archive, output, MaxBackupArchiveBytes, cancellationToken);
            }
            return await RestoreBackupArchiveAsync(archivePath, cancellationToken);
        }
        catch
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
            throw;
        }
    }

    private async Task<OperationView> RestoreBackupArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        var stagingRoot = Utils.GetTempPath($"restore_stage_{Utils.GetGuid(false)}");
        var databaseClosed = false;
        RuntimeOperationCoordinator.Lease? operation = null;
        try
        {
            operation = await _operations.EnterExclusiveAsync(cancellationToken);
            cancellationToken = operation.Token;
            if (!IsSafeBackupArchive(archivePath))
            {
                return OperationView.Fail("backup_archive_invalid", ApiMessageKeys.BackupArchiveInvalid);
            }

            Directory.CreateDirectory(stagingRoot);
            if (!TryExtractBackupConfig(archivePath, stagingRoot)
                || !IsValidBackupConfig(Path.Combine(stagingRoot, Global.ConfigFileName)))
            {
                return OperationView.Fail("backup_archive_invalid", ApiMessageKeys.BackupArchiveInvalid);
            }
            var databasePath = Path.Combine(stagingRoot, "guiNDB.db");
            if (File.Exists(databasePath) && !BackupDatabaseCompatibility.IsCompatible(databasePath, out var databaseError))
            {
                AddLog("backup", $"Restore preflight rejected guiNDB.db: {databaseError}");
                return OperationView.Fail("backup_database_incompatible", ApiMessageKeys.BackupDatabaseIncompatible);
            }

            var (backupResult, safetyBackupPath) = await CreateBackupArchiveAsync();
            if (!backupResult.Success || safetyBackupPath is null)
            {
                return OperationView.Fail("backup_safety_copy_failed", ApiMessageKeys.BackupRestoreFailed);
            }

            await CoreManager.Instance.CoreStop();
            _coreStartedAt = null;
            await ProfileExManager.Instance.SaveTo();
            await StatisticsManager.Instance.SaveTo();
            StatisticsManager.Instance.Close();
            await _mutations.RunAsync(() => EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config)), cancellationToken);
            await SQLiteHelper.Instance.DisposeDbConnectionAsync();
            databaseClosed = true;

            var configPath = Utils.GetConfigPath();
            var configParent = Path.GetDirectoryName(configPath)
                ?? throw new InvalidOperationException("The configuration directory has no parent directory.");
            var candidatePath = Path.Combine(configParent, $".guiConfigs-restore-{Guid.NewGuid():N}");
            var displacedPath = Path.Combine(configParent, $".guiConfigs-before-restore-{Guid.NewGuid():N}");
            try
            {
                PrepareRestoredConfigDirectory(stagingRoot, configPath, candidatePath);
                ReplaceConfigDirectory(candidatePath, configPath, displacedPath);
            }
            finally
            {
                TryDeleteRestoreDirectory(candidatePath, "restore candidate");
                TryDeleteRestoreDirectory(displacedPath, "displaced configuration");
            }

            _restoring = true;
            _operations.RejectNewOperations();
            ScheduleApplicationRestart();
            return OperationView.Ok(ApiMessageKeys.BackupRestoreStarted,
                new { restartRequired = true, safetyBackup = Path.GetFileName(safetyBackupPath) });
        }
        catch (Exception exception)
        {
            AddLog("backup", $"Restore failed: {exception.Message}");
            if (databaseClosed)
            {
                _restoring = true;
                _operations.RejectNewOperations();
                ScheduleApplicationRestart();
            }
            return OperationView.Fail("backup_restore_failed", ApiMessageKeys.BackupRestoreFailed);
        }
        finally
        {
            if (operation is not null)
            {
                await operation.DisposeAsync();
            }
            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, true);
                }
            }
            catch (Exception exception)
            {
                AddLog("backup", $"Restore staging cleanup failed: {exception.Message}");
            }
            finally
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
        }
    }

    private void TryDeleteRestoreDirectory(string path, string description)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception)
        {
            AddLog("backup", $"Restore {description} cleanup failed: {exception.Message}");
        }
    }

    private void ScheduleApplicationRestart()
    {
        lock (_restartGate)
        {
            if (_restartTask is { IsCompleted: false })
            {
                return;
            }
            var stoppingToken = _lifetime.ApplicationStopping;
            _restartTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        _lifetime.StopApplication();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // The host is already stopping.
                }
            });
        }
    }

    private async Task WaitForScheduledRestartAsync(CancellationToken cancellationToken)
    {
        Task? restartTask;
        lock (_restartGate)
        {
            restartTask = _restartTask;
        }
        if (restartTask is not null)
        {
            await restartTask.WaitAsync(cancellationToken);
        }
    }

    internal static bool IsSafeBackupArchive(string archivePath)
    {
        try
        {
            var archiveInfo = new FileInfo(archivePath);
            if (!archiveInfo.Exists || archiveInfo.Length is <= 0 or > MaxBackupArchiveBytes)
            {
                return false;
            }

            using var archive = ZipFile.OpenRead(archivePath);
            var configFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (archive.Entries.Count is 0 or > MaxBackupEntries)
            {
                return false;
            }

            long expandedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length < 0 || entry.Length > MaxBackupExpandedBytes)
                {
                    return false;
                }
                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > MaxBackupExpandedBytes)
                {
                    return false;
                }
            }

            foreach (var entry in archive.Entries)
            {
                var relative = entry.FullName.Replace('\\', '/');
                var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (Path.IsPathRooted(relative)
                    || segments.Any(segment => segment is "." or "..")
                    || relative.Contains(':')
                    || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    return false;
                }

                if (entry.Length == 0)
                {
                    continue;
                }

                var guiConfigIndex = Array.IndexOf(segments, "guiConfigs");
                if (guiConfigIndex < 0 || segments.Length != guiConfigIndex + 2)
                {
                    return false;
                }
                if (!configFiles.Add(segments[^1]))
                {
                    return false;
                }
            }

            return configFiles.Contains(Global.ConfigFileName);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryExtractBackupConfig(string archivePath, string destinationDirectory)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            Directory.CreateDirectory(destinationDirectory);
            foreach (var entry in archive.Entries)
            {
                if (entry.Length == 0)
                {
                    continue;
                }

                var segments = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                var guiConfigIndex = Array.IndexOf(segments, "guiConfigs");
                if (guiConfigIndex < 0 || segments.Length != guiConfigIndex + 2)
                {
                    return false;
                }

                var fileName = segments[^1];
                if (string.Equals(fileName, WebAuthFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var source = entry.Open();
                using var target = new FileStream(
                    Path.Combine(destinationDirectory, fileName),
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                source.CopyTo(target);
            }

            return File.Exists(Path.Combine(destinationDirectory, Global.ConfigFileName));
        }
        catch
        {
            return false;
        }
    }

    internal static void CopyConfigForBackup(string sourceDirectory, string destinationDirectory) =>
        FileUtils.CopyDirectory(sourceDirectory, destinationDirectory, recursive: false, overwrite: true, ignoredName: WebAuthFileName);

    internal static void PrepareRestoredConfigDirectory(
        string extractedConfigDirectory,
        string currentConfigDirectory,
        string candidateDirectory)
    {
        if (Directory.Exists(currentConfigDirectory))
        {
            CopyConfigDirectory(currentConfigDirectory, candidateDirectory);
        }
        else
        {
            Directory.CreateDirectory(candidateDirectory);
        }

        foreach (var file in Directory.EnumerateFiles(extractedConfigDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFileName(file), WebAuthFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(candidateDirectory, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void CopyConfigDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(sourceFile, Path.Combine(destinationDirectory, Path.GetFileName(sourceFile)), overwrite: true);
        }
        foreach (var sourceSubdirectory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            CopyConfigDirectory(sourceSubdirectory, Path.Combine(destinationDirectory, Path.GetFileName(sourceSubdirectory)));
        }
    }

    internal static void ReplaceConfigDirectory(string candidateDirectory, string configDirectory, string displacedDirectory)
    {
        var hadCurrentDirectory = Directory.Exists(configDirectory);
        if (hadCurrentDirectory)
        {
            Directory.Move(configDirectory, displacedDirectory);
        }

        try
        {
            Directory.Move(candidateDirectory, configDirectory);
        }
        catch
        {
            if (hadCurrentDirectory && Directory.Exists(displacedDirectory) && !Directory.Exists(configDirectory))
            {
                Directory.Move(displacedDirectory, configDirectory);
            }
            throw;
        }
    }

    private static bool IsValidBackupConfig(string configPath)
    {
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(configPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && JsonSerializer.Deserialize<Config>(document.RootElement.GetRawText(), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                }) is not null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task CopyWithLimitAsync(Stream input, Stream output, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException("The backup archive exceeds the upload size limit.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
