using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Builder;
using ServiceLib.Handler.Fmt;
using ServiceLib.Manager;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using v2rayN.Web.Contracts;

namespace v2rayN.Web.Services;

public sealed partial class V2rayRuntime
{
    public async Task<IReadOnlyList<ProfileGroupView>> GetProfileGroupsAsync()
    {
        var subscriptions = await AppManager.Instance.SubItems() ?? [];
        var profiles = await AppManager.Instance.ProfileItems(string.Empty) ?? [];
        var counts = profiles.GroupBy(item => item.Subid).ToDictionary(group => group.Key, group => group.Count());
        var groups = new List<ProfileGroupView>
        {
            new(string.Empty, "all", null, ApiMessageKeys.ProfilesAllGroup, profiles.Count, string.IsNullOrEmpty(Config.SubIndexId)),
        };

        groups.AddRange(subscriptions.Select(item => new ProfileGroupView(
            item.Id,
            "subscription",
            item.Remarks,
            null,
            counts.GetValueOrDefault(item.Id),
            item.Id == Config.SubIndexId)));

        return groups;
    }

    public async Task<OperationView> SelectProfileGroupAsync(string? subscriptionId)
    {
        if (!string.IsNullOrWhiteSpace(subscriptionId)
            && await AppManager.Instance.GetSubItem(subscriptionId) is null)
        {
            return OperationView.Fail("subscription_not_found", ApiMessageKeys.SubscriptionNotFound);
        }

        Config.SubIndexId = subscriptionId ?? string.Empty;
        await ConfigHandler.SaveConfig(Config);
        _events.Publish("profiles-changed", new { subscriptionId = Config.SubIndexId });
        return OperationView.Ok(ApiMessageKeys.CommonCompleted, new { subscriptionId = Config.SubIndexId });
    }

    public async Task<OperationView> ImportProfilesAsync(ProfileImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return OperationView.Fail("profile_content_required", ApiMessageKeys.CommonInvalidInput, new { imported = 0 });
        }

        var groupId = request.SubscriptionId ?? Config.SubIndexId ?? string.Empty;
        if (!string.IsNullOrEmpty(groupId) && await AppManager.Instance.GetSubItem(groupId) is null)
        {
            return OperationView.Fail("subscription_not_found", ApiMessageKeys.SubscriptionNotFound, new { imported = 0 });
        }

        var count = await ConfigHandler.AddBatchServers(Config, request.Content, groupId, request.IsSubscription);
        if (count <= 0)
        {
            return OperationView.Fail("profile_import_empty", ApiMessageKeys.ProfileInvalid, new { imported = Math.Max(count, 0) });
        }

        _events.Publish("profiles-changed", new { subscriptionId = groupId });
        AddLog("profile", $"Imported {count} profile(s).");
        return OperationView.Ok(ApiMessageKeys.ProfileImported, new { imported = count });
    }

    public async Task<OperationView> SaveProfileAsync(ProfileItem profile, string? profileId = null)
    {
        if (profile is null)
        {
            return OperationView.Fail("profile_data_required", ApiMessageKeys.CommonInvalidInput);
        }

        var isNew = string.IsNullOrWhiteSpace(profileId);
        if (!isNew)
        {
            var existing = await AppManager.Instance.GetProfileItem(profileId!);
            if (existing is null)
            {
                return OperationView.Fail("profile_not_found", ApiMessageKeys.ProfileNotFound);
            }
            profile.IndexId = profileId!;
            profile.Subid = existing.Subid;
            profile.IsSub = existing.IsSub;
        }
        else
        {
            profile.IndexId = string.Empty;
            profile.Subid = Config.SubIndexId ?? string.Empty;
            profile.IsSub = false;
        }

        if (profile.ConfigType.IsGroupType())
        {
            profile.CoreType ??= ECoreType.Xray;
            var protocolExtra = profile.GetProtocolExtra();
            var childIds = Utils.String2List(protocolExtra.ChildItems) ?? [];
            if (string.IsNullOrWhiteSpace(profile.Remarks)
                || (childIds.Count == 0 && string.IsNullOrWhiteSpace(protocolExtra.SubChildItems))
                || profile.CoreType is not (ECoreType.Xray or ECoreType.sing_box))
            {
                return OperationView.Fail("profile_group_invalid", ApiMessageKeys.ProfileInvalid);
            }
            if (!string.IsNullOrWhiteSpace(protocolExtra.SubChildItems)
                && await AppManager.Instance.GetSubItem(protocolExtra.SubChildItems) is null)
            {
                return OperationView.Fail("subscription_not_found", ApiMessageKeys.SubscriptionNotFound);
            }
            var children = await AppManager.Instance.GetProfileItemsByIndexIds(childIds);
            if (children.Count != childIds.Distinct(StringComparer.Ordinal).Count()
                || await GroupProfileManager.HasCycle(profile))
            {
                return OperationView.Fail("profile_group_children_invalid", ApiMessageKeys.ProfileInvalid);
            }
        }
        else if (profile.ConfigType.IsComplexType() || profile.ConfigType == EConfigType.Outbound)
        {
            return OperationView.Fail("profile_complex_type_requires_import", ApiMessageKeys.CommonInvalidInput);
        }
        else if (!profile.IsValid())
        {
            return OperationView.Fail("profile_validation_failed", ApiMessageKeys.ProfileInvalid);
        }

        var wasCurrent = !isNew && profile.IndexId == Config.IndexId;
        var wasRunning = wasCurrent && _coreStartedAt is not null;
        if (wasRunning)
        {
            await StopCoreAsync(CancellationToken.None);
        }

        var result = profile.ConfigType.IsGroupType()
            ? await ConfigHandler.AddServerCommon(Config, profile)
            : await ConfigHandler.AddServer(Config, profile);
        if (result != 0)
        {
            return OperationView.Fail("profile_save_failed", ApiMessageKeys.CommonInvalidInput);
        }

        if (wasRunning)
        {
            var restart = await StartCoreAsync(profile.IndexId, CancellationToken.None);
            _events.Publish("profiles-changed", new { subscriptionId = Config.SubIndexId });
            return restart.Success
                ? OperationView.Ok(ApiMessageKeys.ProfileSaved, new { profileId = profile.IndexId, coreRestarted = true })
                : OperationView.Fail("profile_saved_core_restart_failed", ApiMessageKeys.ProfileSaved,
                    new { profileId = profile.IndexId, coreRestartRequired = true, coreResultCode = restart.Code });
        }

        _events.Publish("profiles-changed", new { subscriptionId = Config.SubIndexId });
        return OperationView.Ok(ApiMessageKeys.ProfileSaved, new { profileId = profile.IndexId, coreRestarted = false });
    }

    public async Task<OperationView> DeleteProfilesAsync(IEnumerable<string> profileIds)
    {
        var ids = profileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            return OperationView.Fail("profile_selection_required", ApiMessageKeys.ProfileSelectRequired);
        }

        var selected = await AppManager.Instance.GetProfileItemsByIndexIds(ids);
        if (selected.Count == 0)
        {
            return OperationView.Fail("profile_not_found", ApiMessageKeys.ProfileNotFound);
        }

        var removesCurrent = ids.Contains(Config.IndexId, StringComparer.Ordinal);
        var wasRunning = _coreStartedAt is not null;
        if (removesCurrent && wasRunning)
        {
            await StopCoreAsync(CancellationToken.None);
        }

        await ConfigHandler.RemoveServers(Config, selected);
        if (removesCurrent)
        {
            _ = await ConfigHandler.GetDefaultServer(Config);
            await ConfigHandler.SaveConfig(Config);
            if (wasRunning && !string.IsNullOrEmpty(Config.IndexId))
            {
                var restart = await StartCoreAsync(null, CancellationToken.None);
                _events.Publish("profiles-changed", new { subscriptionId = Config.SubIndexId });
                return restart.Success
                    ? OperationView.Ok(ApiMessageKeys.ProfileDeleted, new { deleted = selected.Count, coreRestarted = true })
                    : OperationView.Fail("profiles_deleted_core_restart_failed", ApiMessageKeys.ProfileDeleted,
                        new { deleted = selected.Count, coreRestartRequired = true, coreResultCode = restart.Code });
            }
        }

        _events.Publish("profiles-changed", new { subscriptionId = Config.SubIndexId });
        return OperationView.Ok(ApiMessageKeys.ProfileDeleted, new { deleted = selected.Count });
    }

    public async Task<OperationView> CopyProfilesAsync(IEnumerable<string> profileIds)
    {
        var ids = profileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        var items = await AppManager.Instance.GetProfileItemsOrderedByIndexIds(ids);
        if (items.Count == 0)
        {
            return OperationView.Fail("profile_not_found", ApiMessageKeys.ProfileNotFound);
        }

        await ConfigHandler.CopyServer(Config, items);
        _events.Publish("profiles-changed", new { subscriptionId = Config.SubIndexId });
        return OperationView.Ok(ApiMessageKeys.ProfileCopied, new { copied = items.Count });
    }

    public async Task<OperationView> MoveProfilesToGroupAsync(MoveProfilesRequest request)
    {
        if (!string.IsNullOrEmpty(request.SubscriptionId)
            && await AppManager.Instance.GetSubItem(request.SubscriptionId) is null)
        {
            return OperationView.Fail("subscription_not_found", ApiMessageKeys.SubscriptionNotFound);
        }

        var items = await AppManager.Instance.GetProfileItemsByIndexIds(request.ProfileIds);
        if (items.Count == 0)
        {
            return OperationView.Fail("profile_not_found", ApiMessageKeys.ProfileNotFound);
        }

        await ConfigHandler.MoveToGroup(Config, items, request.SubscriptionId ?? string.Empty);
        _events.Publish("profiles-changed", new { subscriptionId = Config.SubIndexId });
        return OperationView.Ok(ApiMessageKeys.ProfileMoved, new { moved = items.Count, subscriptionId = request.SubscriptionId });
    }

    public async Task<OperationView> MoveProfileAsync(MoveProfileRequest request)
    {
        var groupId = Config.SubIndexId ?? string.Empty;
        var profiles = await GetProfilesAsync(groupId, null);
        var orderedIds = profiles.Select(item => item.IndexId).ToList();
        var index = orderedIds.IndexOf(request.ProfileId);
        if (index < 0)
        {
            return OperationView.Fail("profile_not_in_group", ApiMessageKeys.ProfileNotInGroup);
        }

        var result = await ConfigHandler.MoveServer(Config, orderedIds, index, request.Direction, request.Position);
        _events.Publish("profiles-changed", new { subscriptionId = groupId });
        return result == 0
            ? OperationView.Ok(ApiMessageKeys.ProfileOrderSaved)
            : OperationView.Fail("profile_order_failed", ApiMessageKeys.CommonInvalidInput);
    }

    public async Task<OperationView> SortProfilesAsync(SortProfilesRequest request)
    {
        var groupId = request.SubscriptionId ?? Config.SubIndexId ?? string.Empty;
        if (!Enum.TryParse<EServerColName>(request.Column, true, out _))
        {
            return OperationView.Fail("profile_sort_column_invalid", ApiMessageKeys.CommonInvalidInput);
        }

        var result = await ConfigHandler.SortServers(Config, groupId, request.Column, request.Ascending);
        _events.Publish("profiles-changed", new { subscriptionId = groupId });
        return result == 0
            ? OperationView.Ok(ApiMessageKeys.ProfileOrderSaved)
            : OperationView.Fail("profile_sort_failed", ApiMessageKeys.CommonInvalidInput);
    }

    public async Task<OperationView> RemoveDuplicateProfilesAsync(string? subscriptionId)
    {
        var result = await ConfigHandler.DedupServerList(Config, subscriptionId ?? Config.SubIndexId ?? string.Empty);
        _events.Publish("profiles-changed", new { subscriptionId = subscriptionId ?? Config.SubIndexId });
        return OperationView.Ok(ApiMessageKeys.ProfileDeduplicated, new { removed = result.Item1 - result.Item2 });
    }

    public async Task<OperationView> RemoveInvalidProfilesAsync(string? subscriptionId)
    {
        var count = await ConfigHandler.RemoveInvalidServerResult(Config, subscriptionId ?? Config.SubIndexId ?? string.Empty);
        _events.Publish("profiles-changed", new { subscriptionId = subscriptionId ?? Config.SubIndexId });
        return count < 0
            ? OperationView.Ok(ApiMessageKeys.CommonCompleted, new { removed = 0 })
            : OperationView.Ok(ApiMessageKeys.ProfileInvalidRemoved, new { removed = count });
    }

    public async Task<OperationView> GenerateProfileGroupsAsync(string? subscriptionId, bool byRegion)
    {
        SubItem? subscription = null;
        if (!string.IsNullOrEmpty(subscriptionId))
        {
            subscription = await AppManager.Instance.GetSubItem(subscriptionId);
            if (subscription is null)
            {
                return OperationView.Fail("subscription_not_found", ApiMessageKeys.SubscriptionNotFound);
            }
        }

        var result = byRegion
            ? await ConfigHandler.AddGroupRegionServer(Config, subscription)
            : await ConfigHandler.AddGroupAllServer(Config, subscription);
        if (!result.Success)
        {
            return OperationView.Fail("profile_group_empty", ApiMessageKeys.ProfileGrouped, result.Data);
        }

        _events.Publish("profiles-changed", new { subscriptionId = subscription?.Id ?? string.Empty });
        return OperationView.Ok(ApiMessageKeys.ProfileGrouped, result.Data);
    }

    public async Task<IReadOnlyList<ProfileExportItem>> ExportProfileDataAsync(ProfileExportRequest request)
    {
        var items = await AppManager.Instance.GetProfileItemsOrderedByIndexIds(request.ProfileIds);
        var output = new List<ProfileExportItem>();

        if (request.IncludeShareUris)
        {
            var uris = items.Select(FmtHandler.GetShareUri).Where(uri => !string.IsNullOrWhiteSpace(uri)).ToArray();
            if (uris.Length > 0)
            {
                var content = string.Join(Environment.NewLine, uris) + Environment.NewLine;
                output.Add(new(string.Empty, string.Empty, request.Base64ShareUris ? "share-uri-base64" : "share-uri",
                    request.Base64ShareUris ? Utils.Base64Encode(content) : content));
            }
        }

        if (request.IncludeInnerUri && items.Count > 0)
        {
            var innerUri = InnerFmt.ToUri(items);
            if (!string.IsNullOrWhiteSpace(innerUri))
            {
                output.Add(new(string.Empty, string.Empty, "inner-uri", innerUri));
            }
        }

        if (request.IncludeClientConfig)
        {
            foreach (var item in items)
            {
            var (context, validation) = await CoreConfigContextBuilder.Build(Config, item);
            if (validation.Success)
            {
                var generated = await CoreConfigHandler.GenerateClientConfig(context, null);
                if (generated.Success && generated.Data is string configText)
                {
                    output.Add(new(item.IndexId, item.Remarks, "client-config", configText));
                }
            }
            }
        }
        return output;
    }

    public async Task<string?> GetSubscriptionShareAsync(string id)
    {
        return (await AppManager.Instance.GetSubItem(id))?.Url;
    }

    public async Task<OperationView> StartSpeedTestAsync(SpeedTestRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = request.Action == ESpeedActionType.FastRealping ? ESpeedActionType.Realping : request.Action;
        var profiles = request.ProfileIds is { Length: > 0 }
            ? await AppManager.Instance.GetProfileItemsOrderedByIndexIds(request.ProfileIds)
            : await AppManager.Instance.ProfileItems(Config.SubIndexId ?? string.Empty) ?? [];
        if (profiles.Count == 0)
        {
            return OperationView.Fail("speedtest_profile_selection_required", ApiMessageKeys.SpeedTestSelectRequired);
        }

        if (action != ESpeedActionType.Tcping
            && profiles.Where(item => !item.ConfigType.IsComplexType())
                .Any(item => AppManager.Instance.GetCoreType(item, item.ConfigType) is not (ECoreType.Xray or ECoreType.sing_box)))
        {
            return OperationView.Fail("speedtest_core_unsupported", ApiMessageKeys.SpeedTestUnsupportedCore);
        }

        lock (_speedtestGate)
        {
            if (_speedtestTask is { IsCompleted: false })
            {
                return OperationView.Fail("speedtest_busy", ApiMessageKeys.SpeedTestBusy);
            }
            var speedtestService = _speedtestService ??= CreateSpeedtestService();
            var speedtestCancellation = new CancellationTokenSource();
            _speedtestCancellation = speedtestCancellation;
            _speedtestTask = Task.Run(async () =>
            {
                try
                {
                    using var requested = CancellationTokenSource.CreateLinkedTokenSource(_operations.ShutdownToken, speedtestCancellation.Token);
                    await using var operation = await _operations.EnterOperationAsync(requested.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    await speedtestService.RunLoop(action, profiles, operation.Token);
                }
                catch (OperationCanceledException) when (_operations.IsStopping)
                {
                    // Expected during graceful service shutdown.
                }
                catch (OperationCanceledException) when (speedtestCancellation.IsCancellationRequested)
                {
                    // Expected when the caller stops the active speed test.
                }
                catch (Exception ex)
                {
                    AddLog("speedtest", ex.Message);
                }
                finally
                {
                    lock (_speedtestGate)
                    {
                        if (ReferenceEquals(_speedtestCancellation, speedtestCancellation))
                        {
                            _speedtestCancellation = null;
                        }
                    }
                    speedtestCancellation.Dispose();
                }
            });
        }
        _events.Publish("speedtest-started", new
        {
            code = "speedtest_started",
            messageKey = ApiMessageKeys.SpeedTestStarted,
            action = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(action.ToString()),
            profileIds = profiles.Select(item => item.IndexId),
        });
        return OperationView.Ok(ApiMessageKeys.SpeedTestStarted, new { action = action.ToString(), profileCount = profiles.Count });
    }

    private SpeedtestService CreateSpeedtestService() => new(Config, result =>
    {
        int? delay = int.TryParse(result.Delay, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedDelay)
            ? parsedDelay
            : null;
        decimal? speed = decimal.TryParse(result.Speed, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsedSpeed)
            ? parsedSpeed
            : null;
        _events.Publish("speedtest-result", new
        {
            code = ApiMessageKeys.SpeedTestResult,
            messageKey = ApiMessageKeys.SpeedTestResult,
            indexId = result.IndexId,
            delay,
            speed,
            ipInfo = result.IpInfo,
            rawResult = delay is null && speed is null ? (result.Delay ?? result.Speed) : null,
        });
        if (!string.IsNullOrEmpty(result.IndexId))
        {
            AddLog("speedtest", $"{result.IndexId}: delay={result.Delay}, speed={result.Speed}");
        }
        return Task.CompletedTask;
    });

    public OperationView StopSpeedTests()
    {
        lock (_speedtestGate)
        {
            _speedtestCancellation?.Cancel();
            _speedtestService?.ExitLoop();
        }
        return OperationView.Ok(ApiMessageKeys.CommonCompleted);
    }

    public IReadOnlyList<string> GetRunningSubscriptionUpdates()
    {
        lock (_subscriptionGate)
        {
            return _subscriptionTasks.Where(pair => !pair.Value.IsCompleted).Select(pair => pair.Key).ToArray();
        }
    }
}
