using System.IO.Compression;
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
    public WebDavSettingsView GetWebDavSettings() => new(
        Config.WebDavItem.Url,
        Config.WebDavItem.UserName,
        Config.WebDavItem.DirName,
        !string.IsNullOrEmpty(Config.WebDavItem.Password));

    public async Task<OperationView> UpdateWebDavSettingsAsync(WebDavSettingsInput input)
    {
        Config.WebDavItem.Url = input.Url?.Trim();
        Config.WebDavItem.UserName = input.UserName?.Trim();
        if (input.Password is not null)
        {
            Config.WebDavItem.Password = input.Password;
        }
        Config.WebDavItem.DirName = input.DirName?.Trim();
        await ConfigHandler.SaveConfig(Config);
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
        await ConfigHandler.SaveConfig(Config);
        await ProfileExManager.Instance.SaveTo();
        await StatisticsManager.Instance.SaveTo();

        var archivePath = Utils.GetBackupPath($"backup_{DateTime.UtcNow:yyyyMMddHHmmss}.zip");
        var tempRoot = Utils.GetTempPath($"backup_{Utils.GetGuid(false)}");
        var tempConfigPath = Path.Combine(tempRoot, "guiConfigs");
        try
        {
            FileUtils.CopyDirectory(Utils.GetConfigPath(), tempConfigPath, false, true);
            if (!FileUtils.CreateFromDirectory(tempRoot, archivePath))
            {
                return (OperationView.Fail("backup_create_failed", ApiMessageKeys.BackupArchiveInvalid), null);
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
            return OperationView.Fail("webdav_restore_download_failed", ApiMessageKeys.WebDavRestoreFailed);
        }
        return await RestoreBackupArchiveAsync(archivePath, cancellationToken);
    }

    public async Task<OperationView> RestoreFromUploadAsync(Stream archive, CancellationToken cancellationToken)
    {
        var archivePath = Utils.GetTempPath($"restore_{Utils.GetGuid(false)}.zip");
        await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await archive.CopyToAsync(output, cancellationToken);
        }
        return await RestoreBackupArchiveAsync(archivePath, cancellationToken);
    }

    private async Task<OperationView> RestoreBackupArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        var stagingRoot = Utils.GetTempPath($"restore_stage_{Utils.GetGuid(false)}");
        var databaseClosed = false;
        try
        {
            if (!IsSafeBackupArchive(archivePath))
            {
                return OperationView.Fail("backup_archive_invalid", ApiMessageKeys.BackupArchiveInvalid);
            }

            Directory.CreateDirectory(stagingRoot);
            if (!FileUtils.ZipExtractToFile(archivePath, stagingRoot, string.Empty)
                || !File.Exists(Path.Combine(stagingRoot, Global.ConfigFileName)))
            {
                return OperationView.Fail("backup_archive_invalid", ApiMessageKeys.BackupArchiveInvalid);
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
            await ConfigHandler.SaveConfig(Config);
            await SQLiteHelper.Instance.DisposeDbConnectionAsync();
            databaseClosed = true;

            FileUtils.CopyDirectory(stagingRoot, Utils.GetConfigPath(), false, true);

            _restoring = true;
            ScheduleApplicationRestart();
            return OperationView.Ok(ApiMessageKeys.BackupRestoreStarted,
                new { restartRequired = true, safetyBackup = Path.GetFileName(safetyBackupPath) });
        }
        catch
        {
            if (databaseClosed)
            {
                _restoring = true;
                ScheduleApplicationRestart();
            }
            return OperationView.Fail("backup_restore_failed", ApiMessageKeys.BackupRestoreFailed);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, true);
            }
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
    }

    private void ScheduleApplicationRestart()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            _lifetime.StopApplication();
        });
    }

    private static bool IsSafeBackupArchive(string archivePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var configFiles = new HashSet<string>(StringComparer.Ordinal);
            var files = archive.Entries.Where(entry => entry.Length > 0).ToList();
            if (files.Count == 0)
            {
                return false;
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
                configFiles.Add(segments[^1]);
            }

            return configFiles.Contains(Global.ConfigFileName);
        }
        catch
        {
            return false;
        }
    }
}
