using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Services;
using ServiceLib.Models.Dto;
using v2rayN.Web.Contracts;

namespace v2rayN.Web.Services;

public sealed partial class V2rayRuntime
{
    private Task? _geoUpdateTask;

    public async Task<ApiEnvelope<CoreUpdateCheckView>> CheckXrayUpdateAsync(bool preRelease, bool useProxy, CancellationToken cancellationToken)
    {
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
        if (_xrayUpdateTask is { IsCompleted: false })
        {
            return false;
        }

        _xrayUpdateTask = Task.Run(async () =>
        {
            var result = await UpdateXrayCoreAsync(preRelease, useProxy, CancellationToken.None);
            AddLog("update", result.MessageKey);
            _events.Publish("xray-update-completed", result);
        });
        _events.Publish("xray-update-started", new { core = "xray", code = "xray_update_started", messageKey = ApiMessageKeys.XrayUpdateStarted });
        return true;
    }

    public bool StartGeoUpdate(bool useProxy)
    {
        if (_geoUpdateTask is { IsCompleted: false })
        {
            return false;
        }

        _geoUpdateTask = Task.Run(async () =>
        {
            try
            {
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
                }).UpdateGeoFileAll(useProxy);
            }
            catch (Exception ex)
            {
                AddLog("update", ex.Message);
                _events.Publish("geo-update-completed", OperationView.Fail("geo_update_failed", ApiMessageKeys.XrayUpdateFailed));
            }
        });
        _events.Publish("geo-update-started", new { code = "geo_update_started", messageKey = ApiMessageKeys.GeoUpdateStarted });
        return true;
    }

    private async Task<OperationView> UpdateXrayCoreAsync(bool preRelease, bool useProxy, CancellationToken cancellationToken)
    {
        await _coreGate.WaitAsync(cancellationToken);
        try
        {
            var wasRunning = _coreStartedAt is not null;
            await CoreManager.Instance.CoreStop();
            _coreStartedAt = null;

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
            if (!downloadedArchive.Task.IsCompleted)
            {
                return OperationView.Fail("xray_update_archive_missing", ApiMessageKeys.XrayUpdateFailed);
            }

            var archivePath = await downloadedArchive.Task;
            var installPath = Utils.GetBinPath(string.Empty, ECoreType.Xray.ToString());
            Directory.CreateDirectory(installPath);
            if (archivePath.Contains(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                FileUtils.DecompressTarFile(archivePath, installPath);
                foreach (var directory in new DirectoryInfo(installPath).GetDirectories())
                {
                    FileUtils.CopyDirectory(directory.FullName, installPath, false, true);
                    directory.Delete(true);
                }
            }
            else if (archivePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                FileUtils.DecompressFile(archivePath, installPath, "xray");
            }
            else if (!FileUtils.ZipExtractToFile(archivePath, installPath, "geo"))
            {
                return OperationView.Fail("xray_update_extract_failed", ApiMessageKeys.XrayUpdateFailed);
            }

            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            await CoreManager.Instance.Init(Config, OnCoreMessageAsync);
            _xrayPath = FindXrayExecutable(out _);
            if (_xrayPath is null)
            {
                return OperationView.Fail("xray_binary_missing_after_update", ApiMessageKeys.XrayUpdateFailed);
            }
            if (Utils.IsNonWindows())
            {
                await Utils.SetLinuxChmod(_xrayPath);
            }

            if (wasRunning)
            {
                var current = await AppManager.Instance.GetProfileItem(Config.IndexId);
                if (current is not null)
                {
                    var restart = await StartCoreLockedAsync(current, cancellationToken);
                    return restart.Success
                        ? OperationView.Ok(ApiMessageKeys.XrayUpdateCompleted, new { coreRestarted = true })
                        : OperationView.Ok(ApiMessageKeys.XrayUpdateCompleted, new { coreRestarted = false, coreResultCode = restart.Code });
                }
            }

            return OperationView.Ok(ApiMessageKeys.XrayUpdateCompleted, new { coreRestarted = false });
        }
        catch (Exception ex)
        {
            AddLog("update", $"Xray update failed: {ex.Message}");
            return OperationView.Fail("xray_update_failed", ApiMessageKeys.XrayUpdateFailed);
        }
        finally
        {
            _coreGate.Release();
        }
    }
}
