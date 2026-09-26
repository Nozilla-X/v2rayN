using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Manager;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using ServiceLib.Resx;
using ServiceLib.Services;
using v2rayN.Web.Contracts;

namespace v2rayN.Web.Services;

public sealed partial class V2rayRuntime
{
    private const string GeoFilesUpdateTarget = "GeoFiles";
    private readonly object _updateTaskGate = new();
    private readonly ConcurrentDictionary<ECoreType, Task> _coreUpdateTasks = new();
    private readonly ConcurrentDictionary<string, CoreUpdateProgressView> _updateProgress = new(StringComparer.Ordinal);
    private Task? _geoUpdateTask;

    public CoreUpdateSettingsView GetCoreUpdateSettings()
    {
        var manager = CoreInfoManager.Instance;
        var selectedTypes = Config.CheckUpdateItem.SelectedCoreTypes;
        var targets = GetAvailableWebCoreUpdateTypes()
            .Select(coreType =>
            {
                var isSupported = manager.IsCheckUpdateSupported(coreType)
                    && manager.GetCoreInfo(coreType) is { } info
                    && IsCurrentPlatformDownloadSupported(info);
                var canInstall = isSupported
                    && coreType != ECoreType.v2rayN
                    && CoreUpdatePackageStager.SupportsCore(coreType);
                var unsupportedReason = coreType == ECoreType.v2rayN
                    ? "maintenance.webUpdateUnsupported"
                    : canInstall ? null : "maintenance.updateUnsupported";
                var storageName = coreType.ToString();
                return new CoreUpdateTargetView(
                    storageName,
                    GetCoreUpdateNameKey(coreType),
                    isSupported,
                    canInstall,
                    manager.GetCheckPreRelease(coreType, preRelease: true),
                    selectedTypes?.Contains(storageName, StringComparer.Ordinal) ?? true,
                    unsupportedReason);
            })
            .ToArray();

        return new CoreUpdateSettingsView(
            targets,
            selectedTypes?.Contains(GeoFilesUpdateTarget, StringComparer.Ordinal) ?? true,
            Config.CheckUpdateItem.CheckPreReleaseUpdate,
            Config.CheckUpdateItem.UpdateViaProxy);
    }

    public IReadOnlyList<CoreUpdateProgressView> GetCoreUpdateProgress() =>
        _updateProgress.Values.OrderBy(item => item.CoreType, StringComparer.Ordinal).ToArray();

    public async Task<OperationView> SaveCoreUpdateSettingsAsync(CoreUpdateSettingsInput input)
    {
        var supportedNames = GetAvailableWebCoreUpdateTypes()
            .Select(type => type.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var requestedNames = (input.SelectedCoreTypes ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestedNames.Any(name => name != GeoFilesUpdateTarget && !supportedNames.Contains(name)))
        {
            return OperationView.Fail("core_update_settings_invalid", ApiMessageKeys.CommonInvalidInput);
        }

        await _mutations.RunAsync(async () =>
        {
            var previous = Config.CheckUpdateItem.SelectedCoreTypes ?? [];
            // Keep selections from backups that this platform/Web updater cannot display,
            // just as the desktop updater keeps hidden Core configuration untouched.
            var hiddenSelections = previous.Where(name => name != GeoFilesUpdateTarget && !supportedNames.Contains(name));
            Config.CheckUpdateItem.SelectedCoreTypes = hiddenSelections
                .Concat(requestedNames)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            Config.CheckUpdateItem.CheckPreReleaseUpdate = input.PreRelease;
            Config.CheckUpdateItem.UpdateViaProxy = input.UseProxy;
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        });

        return OperationView.Ok(ApiMessageKeys.CoreUpdateSettingsSaved);
    }

    public async Task<ApiEnvelope<CoreUpdateCheckView>> CheckCoreUpdateAsync(
        ECoreType coreType,
        bool? preRelease,
        bool? useProxy,
        CancellationToken cancellationToken)
    {
        if (!CanCheckCoreUpdate(coreType))
        {
            return ApiEnvelope<CoreUpdateCheckView>.Fail(
                "core_update_unsupported",
                ApiMessageKeys.CoreUpdateUnsupported);
        }

        var checkedUpdate = await CheckCoreUpdateResultAsync(coreType, preRelease, useProxy, cancellationToken);
        var result = checkedUpdate.Result;
        if (result.Success && result.Version is not null)
        {
            return ApiEnvelope<CoreUpdateCheckView>.Ok(
                new CoreUpdateCheckView(true, result.Version.ToString()),
                ApiMessageKeys.CoreUpdateAvailable);
        }
        if (checkedUpdate.IsUpToDate)
        {
            return ApiEnvelope<CoreUpdateCheckView>.Ok(
                new CoreUpdateCheckView(false, result.Version?.ToString(), IsUpToDate: true, Detail: result.Msg),
                ApiMessageKeys.CoreUpdateCurrent);
        }

        AddLog("update", $"{coreType} update check failed: {result.Msg}");
        return ApiEnvelope<CoreUpdateCheckView>.Fail(
            "core_update_check_failed",
            ApiMessageKeys.CoreUpdateCheckFailed,
            new CoreUpdateCheckView(false, null, Detail: result.Msg));
    }

    public Task<ApiEnvelope<CoreUpdateCheckView>> CheckXrayUpdateAsync(
        bool preRelease,
        bool useProxy,
        CancellationToken cancellationToken) =>
        CheckCoreUpdateAsync(ECoreType.Xray, preRelease, useProxy, cancellationToken);

    public OperationView StartCoreUpdate(ECoreType coreType, bool? preRelease = null, bool? useProxy = null)
    {
        if (!CanInstallCoreUpdate(coreType))
        {
            return OperationView.Fail("core_update_unsupported", ApiMessageKeys.CoreUpdateUnsupported);
        }

        lock (_updateTaskGate)
        {
            if (IsUpdateRunningLocked())
            {
                return OperationView.Fail("core_update_busy", ApiMessageKeys.CoreUpdateBusy);
            }

            var selected = Config.CheckUpdateItem.SelectedCoreTypes;
            if (selected is not null && !selected.Contains(coreType.ToString(), StringComparer.Ordinal))
            {
                return OperationView.Fail("core_update_not_selected", ApiMessageKeys.CoreUpdateUnsupported);
            }

            var task = Task.Run(() => RunCoreUpdateAsync(
                coreType,
                preRelease ?? Config.CheckUpdateItem.CheckPreReleaseUpdate,
                useProxy ?? Config.CheckUpdateItem.UpdateViaProxy));
            _coreUpdateTasks[coreType] = task;
        }

        return OperationView.Ok(ApiMessageKeys.CoreUpdateStarted, new { coreType = coreType.ToString() });
    }

    public bool StartXrayUpdate(bool preRelease, bool useProxy) =>
        StartCoreUpdate(ECoreType.Xray, preRelease, useProxy).Success;

    public OperationView StartGeoUpdate(bool? useProxy = null)
    {
        var selected = Config.CheckUpdateItem.SelectedCoreTypes;
        if (selected is not null && !selected.Contains(GeoFilesUpdateTarget, StringComparer.Ordinal))
        {
            return OperationView.Fail("geo_update_not_selected", ApiMessageKeys.GeoUpdateNotSelected);
        }

        lock (_updateTaskGate)
        {
            if (IsUpdateRunningLocked())
            {
                return OperationView.Fail("geo_update_busy", ApiMessageKeys.GeoUpdateBusy);
            }

            var proxy = useProxy ?? Config.CheckUpdateItem.UpdateViaProxy;
            _geoUpdateTask = Task.Run(() => RunGeoUpdateAsync(proxy));
        }
        return OperationView.Ok(ApiMessageKeys.GeoUpdateStarted);
    }

    internal IReadOnlyList<string> GetRunningCoreUpdateOperations()
    {
        lock (_updateTaskGate)
        {
            var operations = _coreUpdateTasks
                .Where(pair => !pair.Value.IsCompleted)
                .Select(pair => GetCoreUpdateOperationName(pair.Key))
                .ToList();
            if (_geoUpdateTask is { IsCompleted: false })
            {
                operations.Add("geo-update");
            }
            return operations;
        }
    }

    private bool IsUpdateRunningLocked() =>
        _coreUpdateTasks.Values.Any(task => !task.IsCompleted)
        || _geoUpdateTask is { IsCompleted: false };

    private IReadOnlyList<ECoreType> GetAvailableWebCoreUpdateTypes()
    {
        var manager = CoreInfoManager.Instance;
        return manager.GetCheckUpdateCoreTypes()
            .Where(coreType => manager.IsCheckUpdateSupported(coreType)
                && manager.GetCoreInfo(coreType) is { } coreInfo
                && IsCurrentPlatformDownloadSupported(coreInfo))
            .ToArray();
    }

    private bool CanCheckCoreUpdate(ECoreType coreType)
    {
        var manager = CoreInfoManager.Instance;
        return manager.GetCheckUpdateCoreTypes().Contains(coreType)
            && manager.IsCheckUpdateSupported(coreType)
            && manager.GetCoreInfo(coreType) is { } coreInfo
            && IsCurrentPlatformDownloadSupported(coreInfo);
    }

    private bool CanInstallCoreUpdate(ECoreType coreType) =>
        coreType != ECoreType.v2rayN
        && CanCheckCoreUpdate(coreType)
        && CoreUpdatePackageStager.SupportsCore(coreType);

    private async Task<CoreUpdateCheckResult> CheckCoreUpdateResultAsync(
        ECoreType coreType,
        bool? preRelease,
        bool? useProxy,
        CancellationToken cancellationToken)
    {
        var updateService = new UpdateService(Config, (_, _) => Task.CompletedTask);
        var result = await updateService.CheckHasUpdateOnly(
            coreType,
            preRelease ?? Config.CheckUpdateItem.CheckPreReleaseUpdate,
            useProxy ?? Config.CheckUpdateItem.UpdateViaProxy,
            cancellationToken);
        return new CoreUpdateCheckResult(result, IsUpToDateResult(coreType, result.Msg));
    }

    private async Task RunCoreUpdateAsync(ECoreType coreType, bool preRelease, bool useProxy)
    {
        CoreUpdateStage? stage = null;
        var wasRunning = _coreStartedAt is not null && AppManager.Instance.RunningCoreType == coreType;
        try
        {
            var result = await CoreUpdateWorkflow.StageThenApplyAsync(
                async () =>
                {
                    await using var operation = await _operations.EnterOperationAsync(_operations.ShutdownToken);
                    PublishCoreUpdateProgress(coreType, "checking", isComplete: false, success: false, wasRunning, null, null);
                    var checkedUpdate = await CheckCoreUpdateResultAsync(coreType, preRelease, useProxy, operation.Token);
                    var check = checkedUpdate.Result;
                    if (checkedUpdate.IsUpToDate)
                    {
                        return new CoreUpdatePrepareResult(null, check);
                    }
                    if (!check.Success || check.Version is null || string.IsNullOrWhiteSpace(check.Url))
                    {
                        throw new InvalidOperationException(check.Msg ?? $"Could not check {coreType} updates.");
                    }

                    stage = await DownloadAndVerifyCoreUpdateAsync(coreType, check, useProxy, operation.Token);
                    return new CoreUpdatePrepareResult(stage, check);
                },
                async prepared =>
                {
                    if (prepared.Stage is null)
                    {
                        return new CoreUpdateApplyResult(true, false, wasRunning, prepared.Check.Msg ?? "Already up to date.", prepared.Check.Version?.ToString());
                    }

                    await using var maintenance = await _operations.EnterExclusiveAsync(
                        _operations.ShutdownToken,
                        allowReadOnlyObservations: true);
                    return await ApplyCoreUpdateAsync(prepared.Stage, maintenance.Token);
                });

            PublishCoreUpdateProgress(
                coreType,
                "completed",
                isComplete: true,
                success: result.Success,
                result.CoreWasRunning,
                result.Version ?? stage?.VersionOutput,
                result.Detail);
            if (!result.Success)
            {
                AddLog("update", $"{coreType} update failed: {result.Detail}");
            }
        }
        catch (OperationCanceledException) when (_operations.IsStopping)
        {
            PublishCoreUpdateProgress(coreType, "failed", isComplete: true, success: false, wasRunning, stage?.VersionOutput, "Canceled during graceful shutdown.");
        }
        catch (Exception exception)
        {
            AddLog("update", $"{coreType} update failed: {exception.Message}");
            PublishCoreUpdateProgress(coreType, "failed", isComplete: true, success: false, wasRunning, stage?.VersionOutput, exception.Message);
        }
        finally
        {
            CleanupCoreUpdateStage(stage);
            lock (_updateTaskGate)
            {
                _coreUpdateTasks.TryRemove(coreType, out _);
            }
        }
    }

    private async Task<CoreUpdateStage> DownloadAndVerifyCoreUpdateAsync(
        ECoreType coreType,
        UpdateResult check,
        bool useProxy,
        CancellationToken cancellationToken)
    {
        var installPath = Path.GetFullPath(Utils.GetBinPath(string.Empty, coreType.ToString()));
        var installParent = Path.GetDirectoryName(installPath)
            ?? throw new InvalidOperationException($"The {coreType} installation path has no parent directory.");
        var archivePath = Utils.GetTempPath($"core-update-{coreType}-{Guid.NewGuid():N}{GetArchiveSuffix(check.Url!)}");
        var packagePath = Path.Combine(installParent, $".{coreType}-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packagePath);

        try
        {
            PublishCoreUpdateProgress(coreType, "downloading", isComplete: false, success: false, IsCoreTypeRunning(coreType), check.Version?.ToString(), check.Url);
            var download = new DownloadService();
            var downloadFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            download.Error += (_, args) => downloadFailure.TrySetResult(args.GetException());
            download.UpdateCompleted += (_, progress) =>
            {
                PublishCoreUpdateProgress(coreType, "downloading", isComplete: false, success: false, IsCoreTypeRunning(coreType), check.Version?.ToString(), progress.Msg);
            };
            await download.DownloadFileAsync(new FileDownloadRequest
            {
                FileUrl = check.Url!,
                FilePath = archivePath,
                DisplayFileName = Path.GetFileName(archivePath),
            },
            useProxy,
            cancellationToken);

            if (downloadFailure.Task.IsCompletedSuccessfully)
            {
                throw new IOException("The core package download failed.", await downloadFailure.Task);
            }
            if (!File.Exists(archivePath))
            {
                throw new IOException("The core package download did not produce an archive.");
            }

            PublishCoreUpdateProgress(coreType, "verifying", isComplete: false, success: false, IsCoreTypeRunning(coreType), check.Version?.ToString(), null);
            await CoreUpdatePackageStager.ExtractAsync(coreType, archivePath, packagePath, cancellationToken);
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType)
                ?? throw new InvalidOperationException($"{coreType} version metadata is unavailable.");
            var stagedExecutable = FindCoreExecutable(coreInfo, packagePath)
                ?? throw new InvalidDataException($"The downloaded {coreType} archive does not contain a usable executable.");
            await Utils.SetLinuxChmod(stagedExecutable);
            var versionOutput = await VerifyCoreExecutableAsync(coreType, coreInfo, stagedExecutable, cancellationToken);

            return new CoreUpdateStage(coreType, installPath, archivePath, packagePath, versionOutput);
        }
        catch
        {
            CleanupCoreUpdateStageFiles(archivePath, packagePath);
            throw;
        }
    }

    private async Task<CoreUpdateApplyResult> ApplyCoreUpdateAsync(CoreUpdateStage stage, CancellationToken cancellationToken)
    {
        await _coreGate.WaitAsync(cancellationToken);
        var installPath = stage.InstallPath;
        var parentDirectory = Path.GetDirectoryName(installPath)
            ?? throw new InvalidOperationException("The Core installation path has no parent directory.");
        var candidatePath = Path.Combine(parentDirectory, $".{stage.CoreType}-candidate-{Guid.NewGuid():N}");
        var backupPath = Path.Combine(parentDirectory, $".{stage.CoreType}-backup-{Guid.NewGuid():N}");
        var hadInstalledCore = Directory.Exists(installPath);
        var originalCoreWasRunning = IsCoreTypeRunning(stage.CoreType);
        ProfileItem? runningProfile = null;
        var oldCoreWasStopped = false;
        var candidateInstalled = false;

        try
        {
            if (originalCoreWasRunning)
            {
                runningProfile = await AppManager.Instance.GetProfileItem(Config.IndexId);
                if (runningProfile is null)
                {
                    return new CoreUpdateApplyResult(false, false, true, "The active profile could not be loaded; the running Core was left unchanged.");
                }
            }

            // Prepare and verify the complete replacement while the serving Core is still
            // alive. This phase performs no network access; proxy-backed downloads and all
            // archive verification have already completed in DownloadAndVerifyCoreUpdateAsync.
            if (hadInstalledCore)
            {
                CopyCoreDirectory(installPath, candidatePath);
            }
            else
            {
                Directory.CreateDirectory(candidatePath);
            }
            foreach (var packageFile in Directory.EnumerateFiles(stage.PackagePath, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(packageFile, Path.Combine(candidatePath, Path.GetFileName(packageFile)), overwrite: true);
            }
            if (stage.CoreType == ECoreType.Xray)
            {
                CopyLatestGeoFilesForApply(installPath, candidatePath);
            }

            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(stage.CoreType)
                ?? throw new InvalidOperationException($"{stage.CoreType} version metadata is unavailable.");
            var candidateExecutable = FindCoreExecutable(coreInfo, candidatePath)
                ?? throw new InvalidDataException($"The prepared {stage.CoreType} directory has no executable.");
            await Utils.SetLinuxChmod(candidateExecutable);
            await VerifyCoreExecutableAsync(stage.CoreType, coreInfo, candidateExecutable, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            PublishCoreUpdateProgress(stage.CoreType, "stopping-core", isComplete: false, success: false, originalCoreWasRunning, stage.VersionOutput,
                originalCoreWasRunning ? null : "The updated Core was not running and will remain stopped.");
            if (originalCoreWasRunning)
            {
                await CoreManager.Instance.CoreStop();
                _coreStartedAt = null;
                oldCoreWasStopped = true;
            }

            PublishCoreUpdateProgress(stage.CoreType, "installing", isComplete: false, success: false, originalCoreWasRunning, stage.VersionOutput, null);
            if (hadInstalledCore)
            {
                Directory.Move(installPath, backupPath);
            }
            try
            {
                Directory.Move(candidatePath, installPath);
                candidateInstalled = true;
            }
            catch
            {
                if (hadInstalledCore && Directory.Exists(backupPath) && !Directory.Exists(installPath))
                {
                    Directory.Move(backupPath, installPath);
                }
                throw;
            }

            await CoreManager.Instance.Init(Config, OnCoreMessageAsync);
            if (stage.CoreType == ECoreType.Xray)
            {
                _xrayPath = FindXrayExecutable(out var missingXrayMessage);
                if (_xrayPath is null)
                {
                    throw new InvalidDataException(missingXrayMessage);
                }
            }

            if (originalCoreWasRunning && runningProfile is not null)
            {
                PublishCoreUpdateProgress(stage.CoreType, "restarting-core", isComplete: false, success: false, true, stage.VersionOutput, null);
                var restart = await StartCoreLockedAsync(runningProfile, cancellationToken);
                if (!restart.Success)
                {
                    throw new InvalidOperationException($"The updated {stage.CoreType} failed to restart ({restart.Code}).");
                }
            }

            if (Directory.Exists(backupPath))
            {
                try
                {
                    Directory.Delete(backupPath, recursive: true);
                }
                catch (Exception exception)
                {
                    AddLog("update", $"{stage.CoreType} updated; previous Core cleanup deferred: {exception.Message}");
                }
            }

            return new CoreUpdateApplyResult(true, false, originalCoreWasRunning, "Update installed and verified.", stage.VersionOutput);
        }
        catch (OperationCanceledException)
        {
            if (candidateInstalled || oldCoreWasStopped || Directory.Exists(backupPath))
            {
                var rolledBack = await RollBackCoreUpdateAsync(
                    stage.CoreType,
                    installPath,
                    backupPath,
                    candidateInstalled,
                    hadInstalledCore,
                    oldCoreWasStopped,
                    originalCoreWasRunning,
                    runningProfile);
                if (!rolledBack)
                {
                    AddLog("update", $"{stage.CoreType} rollback did not complete; previous files remain at {backupPath}.");
                }
            }
            throw;
        }
        catch (Exception exception)
        {
            AddLog("update", $"{stage.CoreType} apply failed: {exception.Message}");
            var rolledBack = true;
            if (candidateInstalled || oldCoreWasStopped || Directory.Exists(backupPath))
            {
                rolledBack = await RollBackCoreUpdateAsync(
                    stage.CoreType,
                    installPath,
                    backupPath,
                    candidateInstalled,
                    hadInstalledCore,
                    oldCoreWasStopped,
                    originalCoreWasRunning,
                    runningProfile);
            }
            var detail = rolledBack
                ? $"{exception.Message} The previous Core was restored."
                : $"{exception.Message} Rollback did not complete; see the runtime log for the retained backup path.";
            return new CoreUpdateApplyResult(false, rolledBack, originalCoreWasRunning, detail, stage.VersionOutput);
        }
        finally
        {
            try
            {
                if (Directory.Exists(candidatePath))
                {
                    Directory.Delete(candidatePath, recursive: true);
                }
            }
            catch (Exception exception)
            {
                AddLog("update", $"{stage.CoreType} candidate cleanup failed: {exception.Message}");
            }
            _coreGate.Release();
        }
    }

    private async Task<bool> RollBackCoreUpdateAsync(
        ECoreType coreType,
        string installPath,
        string backupPath,
        bool candidateInstalled,
        bool hadInstalledCore,
        bool oldCoreWasStopped,
        bool wasRunning,
        ProfileItem? runningProfile)
    {
        try
        {
            // Do not stop an unrelated active Core when the target being updated was idle.
            if (oldCoreWasStopped && wasRunning)
            {
                await CoreManager.Instance.CoreStop();
                _coreStartedAt = null;
            }
            if (candidateInstalled && Directory.Exists(installPath))
            {
                Directory.Delete(installPath, recursive: true);
            }
            if (hadInstalledCore && Directory.Exists(backupPath) && !Directory.Exists(installPath))
            {
                Directory.Move(backupPath, installPath);
            }

            await CoreManager.Instance.Init(Config, OnCoreMessageAsync);
            if (coreType == ECoreType.Xray)
            {
                _xrayPath = FindXrayExecutable(out _);
            }
            if (wasRunning && runningProfile is not null)
            {
                PublishCoreUpdateProgress(coreType, "restarting-core", isComplete: false, success: false, true, null, "Restarting the restored Core.");
                return (await StartCoreLockedAsync(runningProfile, CancellationToken.None)).Success;
            }
            return true;
        }
        catch (Exception exception)
        {
            AddLog("update", $"{coreType} rollback failed: {exception.Message}");
            return false;
        }
    }

    private async Task<string> VerifyCoreExecutableAsync(
        ECoreType coreType,
        CoreInfo coreInfo,
        string executablePath,
        CancellationToken cancellationToken)
    {
        var versionArgument = coreInfo.VersionArg
            ?? throw new InvalidDataException($"{coreType} has no version-check command configured.");
        var versionOutput = await Utils.GetCliWrapOutput(executablePath, versionArgument, cancellationToken);
        var expectedName = coreInfo.Match ?? coreType.ToString();
        if (string.IsNullOrWhiteSpace(versionOutput)
            || !versionOutput.Contains(expectedName, StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(versionOutput, @"\b\d+\.\d+\.\d+\b", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException($"The staged {coreType} executable did not pass its version check.");
        }
        return versionOutput.Trim();
    }

    private static string? FindCoreExecutable(CoreInfo coreInfo, string directory) =>
        (coreInfo.CoreExes ?? [])
            .Select(name => Path.Combine(directory, Utils.GetExeName(name)))
            .FirstOrDefault(File.Exists);

    private static bool IsCurrentPlatformDownloadSupported(CoreInfo coreInfo)
    {
        var downloadUrl = Utils.IsWindows()
            ? RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => coreInfo.DownloadUrlWinArm64,
                Architecture.X64 => coreInfo.DownloadUrlWin64,
                _ => null,
            }
            : Utils.IsLinux()
                ? RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.Arm64 => coreInfo.DownloadUrlLinuxArm64,
                    Architecture.RiscV64 => coreInfo.DownloadUrlLinuxRiscV64,
                    Architecture.LoongArch64 => coreInfo.DownloadUrlLinuxLoong64,
                    Architecture.X64 => coreInfo.DownloadUrlLinux64,
                    _ => null,
                }
                : Utils.IsMacOS()
                    ? RuntimeInformation.ProcessArchitecture switch
                    {
                        Architecture.Arm64 => coreInfo.DownloadUrlOSXArm64,
                        Architecture.X64 => coreInfo.DownloadUrlOSX64,
                        _ => null,
                    }
                    : null;

        return !string.IsNullOrWhiteSpace(downloadUrl);
    }

    internal static bool IsUpToDateResult(ECoreType coreType, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        const string typeMarker = "__V2RAYN_CORE_TYPE__";
        const string versionMarker = "__V2RAYN_CORE_VERSION__";
        var template = string.Format(ResUI.IsLatestCore, typeMarker, versionMarker);
        var typePosition = template.IndexOf(typeMarker, StringComparison.Ordinal);
        var versionPosition = template.IndexOf(versionMarker, StringComparison.Ordinal);
        if (typePosition < 0 || versionPosition <= typePosition)
        {
            return false;
        }

        var expectedPrefix = template[..typePosition]
            + coreType
            + template[(typePosition + typeMarker.Length)..versionPosition];
        var suffix = template[(versionPosition + versionMarker.Length)..];
        var normalizedMessage = message.Trim();
        if (!normalizedMessage.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || !normalizedMessage.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var versionLength = normalizedMessage.Length - expectedPrefix.Length - suffix.Length;
        return versionLength > 0
            && normalizedMessage.Substring(expectedPrefix.Length, versionLength).All(character => !char.IsWhiteSpace(character));
    }

    private bool IsCoreTypeRunning(ECoreType coreType) =>
        CoreUpdateRuntimePolicy.IsTargetRunning(coreType, AppManager.Instance.RunningCoreType, _coreStartedAt);

    private static string GetArchiveSuffix(string downloadUrl)
    {
        var path = Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : downloadUrl;
        return path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? ".tar.gz"
            : Path.GetExtension(path);
    }

    private static void CopyCoreDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(sourceFile, Path.Combine(destinationDirectory, Path.GetFileName(sourceFile)), overwrite: true);
        }
        foreach (var sourceSubdirectory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            CopyCoreDirectory(sourceSubdirectory, Path.Combine(destinationDirectory, Path.GetFileName(sourceSubdirectory)));
        }
    }

    private void CleanupCoreUpdateStage(CoreUpdateStage? stage)
    {
        if (stage is not null)
        {
            CleanupCoreUpdateStageFiles(stage.ArchivePath, stage.PackagePath);
        }
    }

    private void CleanupCoreUpdateStageFiles(string archivePath, string packagePath)
    {
        try
        {
            if (Directory.Exists(packagePath))
            {
                Directory.Delete(packagePath, recursive: true);
            }
        }
        catch (Exception exception)
        {
            AddLog("update", $"Core package staging cleanup failed: {exception.Message}");
        }
        try
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
        catch (Exception exception)
        {
            AddLog("update", $"Core archive cleanup failed: {exception.Message}");
        }
    }

    private async Task RunGeoUpdateAsync(bool useProxy)
    {
        try
        {
            PublishGeoUpdateProgress("downloading", isComplete: false, success: false, null);
            await using var operation = await _operations.EnterExclusiveAsync(
                _operations.ShutdownToken,
                allowReadOnlyObservations: true);
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
                return Task.CompletedTask;
            }).UpdateGeoFileAll(useProxy, operation.Token);
            PublishGeoUpdateProgress("completed", isComplete: true, success: true, null);
        }
        catch (OperationCanceledException) when (_operations.IsStopping)
        {
            PublishGeoUpdateProgress("failed", isComplete: true, success: false, "Canceled during graceful shutdown.");
        }
        catch (Exception exception)
        {
            AddLog("update", $"GeoFiles update failed: {exception.Message}");
            PublishGeoUpdateProgress("failed", isComplete: true, success: false, exception.Message);
        }
        finally
        {
            lock (_updateTaskGate)
            {
                _geoUpdateTask = null;
            }
        }
    }

    private void PublishCoreUpdateProgress(
        ECoreType coreType,
        string phase,
        bool isComplete,
        bool success,
        bool coreWasRunning,
        string? version,
        string? detail)
    {
        var progress = new CoreUpdateProgressView(coreType.ToString(), phase, isComplete, success, coreWasRunning, version, detail);
        if (!_updateProgress.TryGetValue(coreType.ToString(), out var previous) || previous.Phase != phase)
        {
            AddLog("update", $"{coreType} update phase: {phase}");
        }
        _updateProgress[coreType.ToString()] = progress;
        _events.Publish("core-update-progress", progress);
        if (coreType == ECoreType.Xray && isComplete)
        {
            _events.Publish("xray-update-completed", success
                ? OperationView.Ok(ApiMessageKeys.XrayUpdateCompleted, new { version, coreRestarted = coreWasRunning })
                : OperationView.Fail("xray_update_failed", ApiMessageKeys.XrayUpdateFailed, new { coreWasRunning, detail }));
        }
    }

    private void PublishGeoUpdateProgress(string phase, bool isComplete, bool success, string? detail)
    {
        var progress = new CoreUpdateProgressView(GeoFilesUpdateTarget, phase, isComplete, success, false, null, detail);
        if (!_updateProgress.TryGetValue(GeoFilesUpdateTarget, out var previous) || previous.Phase != phase)
        {
            AddLog("update", $"GeoFiles update phase: {phase}");
        }
        _updateProgress[GeoFilesUpdateTarget] = progress;
        _events.Publish("core-update-progress", progress);
        if (isComplete)
        {
            _events.Publish("geo-update-completed", success
                ? OperationView.Ok(ApiMessageKeys.CommonCompleted)
                : OperationView.Fail("geo_update_failed", ApiMessageKeys.CoreUpdateFailed));
        }
    }

    private static string GetCoreUpdateOperationName(ECoreType coreType) =>
        $"core-update-{coreType.ToString().ToLowerInvariant()}";

    private static string GetCoreUpdateNameKey(ECoreType coreType) => coreType switch
    {
        ECoreType.Xray => "maintenance.coreNames.xray",
        ECoreType.mihomo => "maintenance.coreNames.mihomo",
        ECoreType.sing_box => "maintenance.coreNames.singBox",
        ECoreType.v2rayN => "maintenance.coreNames.v2rayN",
        _ => "maintenance.coreNames.other",
    };

    internal static void CopyLatestGeoFilesForApply(string installPath, string stagingPath)
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

    internal sealed record CoreUpdateStage(ECoreType CoreType, string InstallPath, string ArchivePath, string PackagePath, string VersionOutput);
    private sealed record CoreUpdateCheckResult(UpdateResult Result, bool IsUpToDate);
    private sealed record CoreUpdatePrepareResult(CoreUpdateStage? Stage, UpdateResult Check);
    private sealed record CoreUpdateApplyResult(bool Success, bool RolledBack, bool CoreWasRunning, string Detail, string? Version = null);
}

internal static class CoreUpdateRuntimePolicy
{
    public static bool IsTargetRunning(ECoreType target, ECoreType runningCore, DateTimeOffset? startedAt) =>
        startedAt is not null && target == runningCore;
}

internal static class CoreUpdateWorkflow
{
    public static async Task<TResult> StageThenApplyAsync<TStage, TResult>(
        Func<Task<TStage>> stageAsync,
        Func<TStage, Task<TResult>> applyAsync)
    {
        var staged = await stageAsync();
        return await applyAsync(staged);
    }
}
