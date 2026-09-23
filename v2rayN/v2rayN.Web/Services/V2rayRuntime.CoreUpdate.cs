using System.IO.Compression;
using System.Text.RegularExpressions;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Manager;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using v2rayN.Web.Contracts;

namespace v2rayN.Web.Services;

public sealed partial class V2rayRuntime
{
    private readonly object _updateTaskGate = new();
    private Task? _geoUpdateTask;

    public async Task<ApiEnvelope<CoreUpdateCheckView>> CheckXrayUpdateAsync(bool preRelease, bool useProxy, CancellationToken cancellationToken)
    {
        // HTTP calls are protected by the request middleware. Background checks acquire
        // their own lease at the task boundary and must not nest a shared lease here.
        var result = await new UpdateService(Config, (_, _) => Task.CompletedTask)
            .CheckHasUpdateOnly(ECoreType.Xray, preRelease, useProxy, cancellationToken);
        if (!result.Success)
        {
            AddLog("update", result.Msg ?? ApiMessageKeys.XrayUpdateCheckFailed);
            return ApiEnvelope<CoreUpdateCheckView>.Fail("xray_update_check_failed", ApiMessageKeys.XrayUpdateCheckFailed,
                new CoreUpdateCheckView(false, null));
        }

        return ApiEnvelope<CoreUpdateCheckView>.Ok(
            new CoreUpdateCheckView(result.Version is not null, result.Version?.ToString()),
            result.Version is null ? ApiMessageKeys.XrayUpdateCurrent : ApiMessageKeys.XrayUpdateAvailable);
    }

    public bool StartXrayUpdate(bool preRelease, bool useProxy)
    {
        lock (_updateTaskGate)
        {
            if (IsUpdateRunning())
            {
                return false;
            }

            _xrayUpdateTask = Task.Run(async () =>
            {
                XrayUpdateStage? stage = null;
                try
                {
                    await using (var operation = await _operations.EnterOperationAsync(_operations.ShutdownToken))
                    {
                        var staged = await StageXrayCoreUpdateAsync(preRelease, useProxy, operation.Token);
                        if (staged.Failure is not null)
                        {
                            AddLog("update", staged.Failure.MessageKey);
                            _events.Publish("xray-update-completed", staged.Failure);
                            return;
                        }
                        stage = staged.Stage;
                    }

                    await using var maintenance = await _operations.EnterExclusiveAsync(_operations.ShutdownToken);
                    var result = await ApplyXrayCoreUpdateAsync(stage!, maintenance.Token);
                    AddLog("update", result.MessageKey);
                    _events.Publish("xray-update-completed", result);
                }
                catch (OperationCanceledException) when (_operations.IsStopping)
                {
                    // Expected during graceful service shutdown.
                }
                catch (Exception ex)
                {
                    AddLog("update", $"Xray update failed: {ex.Message}");
                    _events.Publish("xray-update-completed", OperationView.Fail("xray_update_failed", ApiMessageKeys.XrayUpdateFailed));
                }
                finally
                {
                    CleanupXrayStage(stage);
                }
            });
        }

        _events.Publish("xray-update-started", new { core = "xray", code = "xray_update_started", messageKey = ApiMessageKeys.XrayUpdateStarted });
        return true;
    }

    public bool StartGeoUpdate(bool useProxy)
    {
        lock (_updateTaskGate)
        {
            if (IsUpdateRunning())
            {
                return false;
            }

            _geoUpdateTask = Task.Run(async () =>
            {
                try
                {
                    await using var operation = await _operations.EnterExclusiveAsync(_operations.ShutdownToken);
                    await new UpdateService(Config, (success, message) =>
                    {
                        AddLog("update", message);
                        _events.Publish("geo-update-progress", new
                        {
                            success,
                            code = success ? "ok" : "geo_update_progress",
                            messageKey = success ? ApiMessageKeys.CommonCompleted : ApiMessageKeys.GeoUpdateProgress,
                            rawLog = message,
                        });
                        if (success)
                        {
                            _events.Publish("geo-update-completed", OperationView.Ok(ApiMessageKeys.CommonCompleted));
                        }
                        return Task.CompletedTask;
                    }).UpdateGeoFileAll(useProxy, operation.Token);
                }
                catch (OperationCanceledException) when (_operations.IsStopping)
                {
                    // Expected during graceful service shutdown.
                }
                catch (Exception ex)
                {
                    AddLog("update", ex.Message);
                    _events.Publish("geo-update-completed", OperationView.Fail("geo_update_failed", ApiMessageKeys.XrayUpdateFailed));
                }
            });
        }

        _events.Publish("geo-update-started", new { code = "geo_update_started", messageKey = ApiMessageKeys.GeoUpdateStarted });
        return true;
    }

    private bool IsUpdateRunning() =>
        (_xrayUpdateTask is { IsCompleted: false }) || (_geoUpdateTask is { IsCompleted: false });

    private sealed record XrayUpdateStage(string InstallPath, string ArchivePath, string StagingPath, string VersionOutput);

    private async Task<(XrayUpdateStage? Stage, OperationView? Failure)> StageXrayCoreUpdateAsync(
        bool preRelease,
        bool useProxy,
        CancellationToken cancellationToken)
    {
        var installPath = Path.GetFullPath(Utils.GetBinPath(string.Empty, ECoreType.Xray.ToString()));
        string? archivePath = null;
        string? stagingPath = null;
        try
        {
            // Keep the active Xray listening while ServiceLib checks/downloads through the
            // saved local mixed proxy. A proxy-enabled update must not stop its own proxy.
            var downloadedArchive = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var updateService = new UpdateService(Config, (success, message) =>
            {
                AddLog("update", message);
                if (success && File.Exists(message))
                {
                    downloadedArchive.TrySetResult(message);
                }
                return Task.CompletedTask;
            });

            await updateService.CheckUpdateCore(ECoreType.Xray, preRelease, useProxy, cancellationToken);
            if (!downloadedArchive.Task.IsCompletedSuccessfully)
            {
                return (null, OperationView.Fail("xray_update_download_failed", ApiMessageKeys.XrayUpdateFailed));
            }

            archivePath = await downloadedArchive.Task;
            var parentDirectory = Path.GetDirectoryName(installPath)
                ?? throw new InvalidOperationException("The Xray install path has no parent directory.");
            Directory.CreateDirectory(parentDirectory);
            stagingPath = Path.Combine(parentDirectory, $".Xray-stage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingPath);

            await ExtractXrayArchiveAsync(archivePath, stagingPath, cancellationToken);
            CopyExistingGeoFiles(installPath, stagingPath);

            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(ECoreType.Xray)
                ?? throw new InvalidOperationException("Xray core metadata is unavailable.");
            var stagedExecutable = (coreInfo.CoreExes ?? [])
                .Select(name => Path.Combine(stagingPath, Utils.GetExeName(name)))
                .FirstOrDefault(File.Exists)
                ?? throw new InvalidDataException("The downloaded Xray archive does not contain the Xray executable.");

            await Utils.SetLinuxChmod(stagedExecutable);
            var versionOutput = await Utils.GetCliWrapOutput(stagedExecutable, coreInfo.VersionArg, cancellationToken);
            if (string.IsNullOrWhiteSpace(versionOutput)
                || !versionOutput.Contains(coreInfo.Match ?? "Xray", StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(versionOutput, @"\b\d+\.\d+\.\d+\b", RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException("The staged Xray executable did not pass its version check.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var stage = new XrayUpdateStage(installPath, archivePath, stagingPath, versionOutput.Trim());
            archivePath = null;
            stagingPath = null;
            return (stage, null);
        }
        finally
        {
            CleanupXrayStageFiles(archivePath, stagingPath);
        }
    }

    private async Task<OperationView> ApplyXrayCoreUpdateAsync(XrayUpdateStage stage, CancellationToken cancellationToken)
    {
        await _coreGate.WaitAsync(cancellationToken);
        string? backupPath = null;
        var replaced = false;
        var stoppedXray = false;
        var wasXrayRunning = false;
        ProfileItem? runningProfile = null;
        try
        {
            wasXrayRunning = _coreStartedAt is not null && AppManager.Instance.RunningCoreType == ECoreType.Xray;
            runningProfile = wasXrayRunning
                ? await AppManager.Instance.GetProfileItem(Config.IndexId)
                : null;
            if (wasXrayRunning && runningProfile is null)
            {
                return OperationView.Fail("xray_update_profile_missing", ApiMessageKeys.XrayUpdateFailed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (wasXrayRunning)
            {
                await CoreManager.Instance.CoreStop();
                _coreStartedAt = null;
                stoppedXray = true;
            }

            var parentDirectory = Path.GetDirectoryName(stage.InstallPath)
                ?? throw new InvalidOperationException("The Xray install path has no parent directory.");
            backupPath = Path.Combine(parentDirectory, $".Xray-backup-{Guid.NewGuid():N}");
            if (Directory.Exists(stage.InstallPath))
            {
                Directory.Move(stage.InstallPath, backupPath);
            }
            try
            {
                Directory.Move(stage.StagingPath, stage.InstallPath);
                replaced = true;
            }
            catch
            {
                if (Directory.Exists(backupPath) && !Directory.Exists(stage.InstallPath))
                {
                    Directory.Move(backupPath, stage.InstallPath);
                }
                throw;
            }

            await CoreManager.Instance.Init(Config, OnCoreMessageAsync);
            _xrayPath = FindXrayExecutable(out var missingXrayMessage);
            if (_xrayPath is null)
            {
                throw new InvalidDataException(missingXrayMessage);
            }

            if (wasXrayRunning && runningProfile is not null)
            {
                var restart = await StartCoreLockedAsync(runningProfile, cancellationToken);
                if (!restart.Success)
                {
                    throw new InvalidOperationException($"The updated Xray failed to start ({restart.Code}).");
                }
            }

            if (backupPath is not null && Directory.Exists(backupPath))
            {
                try
                {
                    Directory.Delete(backupPath, recursive: true);
                    backupPath = null;
                }
                catch (Exception ex)
                {
                    AddLog("update", $"Xray updated; previous core cleanup deferred: {ex.Message}");
                }
            }

            return OperationView.Ok(ApiMessageKeys.XrayUpdateCompleted,
                new { coreRestarted = wasXrayRunning && runningProfile is not null, version = stage.VersionOutput });
        }
        catch (OperationCanceledException)
        {
            if (replaced || stoppedXray)
            {
                await RollBackXrayAsync(stage.InstallPath, backupPath, replaced, wasXrayRunning, runningProfile);
            }
            throw;
        }
        catch (Exception ex)
        {
            AddLog("update", $"Xray update failed: {ex.Message}");
            var rolledBack = true;
            if (replaced || stoppedXray)
            {
                rolledBack = await RollBackXrayAsync(stage.InstallPath, backupPath, replaced, wasXrayRunning, runningProfile);
            }
            return OperationView.Fail("xray_update_failed", ApiMessageKeys.XrayUpdateFailed,
                new { rolledBack, coreRestarted = wasXrayRunning && rolledBack });
        }
        finally
        {
            _coreGate.Release();
        }
    }

    private void CleanupXrayStage(XrayUpdateStage? stage)
    {
        if (stage is not null)
        {
            CleanupXrayStageFiles(stage.ArchivePath, stage.StagingPath);
        }
    }

    private void CleanupXrayStageFiles(string? archivePath, string? stagingPath)
    {
        try
        {
            if (stagingPath is not null && Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
        catch (Exception ex)
        {
            AddLog("update", $"Xray staging cleanup failed: {ex.Message}");
        }
        try
        {
            if (archivePath is not null && File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
        catch (Exception ex)
        {
            AddLog("update", $"Xray archive cleanup failed: {ex.Message}");
        }
    }

    private async Task<bool> RollBackXrayAsync(string installPath, string? backupPath, bool replaced, bool wasRunning, ProfileItem? runningProfile)
    {
        try
        {
            await CoreManager.Instance.CoreStop();
            _coreStartedAt = null;
            if (replaced && Directory.Exists(installPath))
            {
                Directory.Delete(installPath, recursive: true);
            }
            if (backupPath is not null && Directory.Exists(backupPath))
            {
                if (Directory.Exists(installPath))
                {
                    Directory.Delete(installPath, recursive: true);
                }
                Directory.Move(backupPath, installPath);
            }

            await CoreManager.Instance.Init(Config, OnCoreMessageAsync);
            _xrayPath = FindXrayExecutable(out _);
            if (wasRunning && runningProfile is not null && _xrayPath is not null)
            {
                var restart = await StartCoreLockedAsync(runningProfile, CancellationToken.None);
                return restart.Success;
            }
            return !wasRunning || runningProfile is null || _xrayPath is not null;
        }
        catch (Exception ex)
        {
            AddLog("update", $"Xray rollback failed: {ex.Message}");
            return false;
        }
    }

    private static async Task ExtractXrayArchiveAsync(string archivePath, string stagingPath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.Where(entry => entry.Length > 0).ToArray();
        if (entries.Length is 0 or > 4096 || entries.Sum(entry => entry.Length) > 512L * 1024 * 1024)
        {
            throw new InvalidDataException("The Xray archive has an invalid file count or expanded size.");
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = entry.FullName.Replace('\\', '/');
            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (Path.IsPathRooted(normalized)
                || segments.Length == 0
                || segments.Any(segment => segment is "." or "..")
                || normalized.Contains(':')
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw new InvalidDataException("The Xray archive contains an unsafe path.");
            }

            // Xray's release zip has a flat payload. Flattening mirrors the existing
            // desktop updater and avoids trusting any archive directory layout.
            var fileName = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var destination = Path.Combine(stagingPath, fileName);
            await using var source = entry.Open();
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
        }
    }

    private static void CopyExistingGeoFiles(string installPath, string stagingPath)
    {
        if (!Directory.Exists(installPath))
        {
            return;
        }
        foreach (var source in Directory.EnumerateFiles(installPath, "geo*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(source, Path.Combine(stagingPath, Path.GetFileName(source)), overwrite: true);
        }
    }
}
