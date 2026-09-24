using System.Net.Sockets;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Builder;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using NLog;
using NLog.Config;
using NLog.Targets;
using v2rayN.Web.Adapters;
using v2rayN.Web.Contracts;

namespace v2rayN.Web.Services;

public sealed partial class V2rayRuntime(
    EventHub events,
    LogBuffer logs,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    RuntimeOperationCoordinator operations)
{
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(20);
    private readonly EventHub _events = events;
    private readonly LogBuffer _logs = logs;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHostApplicationLifetime _lifetime = lifetime;
    private readonly RuntimeOperationCoordinator _operations = operations;
    private readonly RuntimeMutationGate _mutations = new();
    private readonly SemaphoreSlim _coreGate = new(1, 1);
    private readonly object _subscriptionGate = new();
    private readonly object _speedtestGate = new();
    private readonly Dictionary<string, Task> _subscriptionTasks = new(StringComparer.Ordinal);
    private SpeedtestService? _speedtestService;
    private Task? _speedtestTask;
    private CancellationTokenSource? _speedtestCancellation;
    private Task? _xrayUpdateTask;
    private DateTimeOffset? _coreStartedAt;
    private string? _xrayPath;
    private ServerSpeedItem? _latestTraffic;
    private bool _initialized;
    private bool _restoring;

    private Config Config => AppManager.Instance.Config;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!AppManager.Instance.InitApp())
        {
            throw new InvalidOperationException("ServiceLib could not load its configuration.");
        }
        ConfigureServiceLibConsoleLogging();

        AppManager.Instance.WindowDialog = new HeadlessWindowDialog();
        if (!AppManager.Instance.InitComponents())
        {
            throw new InvalidOperationException("ServiceLib component initialization failed.");
        }

        await _mutations.RunAsync(async () =>
        {
            EnsureInboundDefaults(Config);
            EnsureCoreTypeMappings();
            var proxyPortOverride = _configuration.GetValue<int?>("V2RAYN_WEB_PROXY_PORT");
            if (proxyPortOverride is <= 0 or > 65535)
            {
                throw new InvalidOperationException("V2RAYN_WEB_PROXY_PORT must be a valid TCP/UDP port (1-65535).");
            }
            if (proxyPortOverride is int configuredProxyPort)
            {
                Config.Inbound[0].LocalPort = configuredProxyPort;
            }
            if (_configuration.GetValue<bool?>("V2RAYN_WEB_PROXY_LISTEN_ALL") is bool allowProxyFromLan)
            {
                Config.Inbound[0].AllowLANConn = allowProxyFromLan;
            }
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        });
        await ConfigHandler.InitBuiltinDNS(Config);
        await ConfigHandler.InitBuiltinFullConfigTemplate(Config);
        if ((await AppManager.Instance.RoutingItems() ?? []).Count == 0)
        {
            await ConfigHandler.InitBuiltinRouting(Config);
        }
        await ProfileExManager.Instance.Init();
        await CertPemManager.Instance.Init(Config);
        // ServiceLib remains unchanged; this frontend rejects generated launch contexts that enable TUN.
        await CoreManager.Instance.Init(Config, OnCoreMessageAsync);
        if (Config.GuiItem.EnableStatistics || Config.GuiItem.DisplayRealTimeSpeed)
        {
            await StatisticsManager.Instance.Init(Config, OnStatisticsUpdateAsync);
        }
        _xrayPath = FindXrayExecutable(out var missingXrayMessage);
        if (_xrayPath is null)
        {
            AddLog("core", missingXrayMessage);
        }

        _initialized = true;
        AddLog("web", "serviceLib.initialized");
        StartScheduledOperations(cancellationToken);

        if (_configuration.GetValue("V2RAYN_WEB_AUTOSTART", false))
        {
            var result = await StartCoreAsync(null, cancellationToken);
            if (!result.Success)
            {
                AddLog("core", result.MessageKey);
            }
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        var steps = new List<ShutdownCleanupStep>
        {
            new("scheduled restart", async deadline =>
            {
                await WaitForScheduledRestartAsync(deadline.Token);
                return true;
            }),
            new("scheduled operations stop", deadline => StopScheduledOperationsAsync(deadline.Token)),
            new("operation drain", async deadline =>
            {
                var drained = await _operations.StopAndDrainAsync(deadline.Token, deadline.Remaining);
                if (!drained)
                {
                    AddLog("web", "Shutdown operation drain timed out; skipped all Core, state-save, and SQLite cleanup.");
                }
                return drained;
            }),
        };

        var saveServiceLibState = _initialized && !_restoring;
        if (saveServiceLibState)
        {
            steps.Add(new("Core stop", async _ =>
            {
                await CoreManager.Instance.CoreStop();
                return true;
            }));
            steps.Add(new("profile save", async _ =>
            {
                await ProfileExManager.Instance.SaveTo();
                return true;
            }));
            steps.Add(new("statistics save", async _ =>
            {
                await StatisticsManager.Instance.SaveTo();
                return true;
            }));
            steps.Add(new("statistics close", _ =>
            {
                StatisticsManager.Instance.Close();
                return Task.FromResult(true);
            }));
            steps.Add(new("configuration save", async deadline =>
            {
                await _mutations.RunAsync(
                    () => EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config)),
                    deadline.Token);
                return true;
            }));
            steps.Add(new("database close", async _ =>
            {
                await SQLiteHelper.Instance.DisposeDbConnectionAsync();
                return true;
            }));
        }

        var completed = await ShutdownCleanupSequence.RunAsync(
            steps,
            ShutdownBudget,
            cancellationToken,
            message => AddLog("web", message));
        if (completed && saveServiceLibState)
        {
            AddLog("web", "serviceLib.stopped");
        }
    }

    internal static async Task EnsureConfigSaveSucceededAsync(Func<Task<int>> save)
    {
        if (await save() != 0)
        {
            throw new IOException("ServiceLib could not persist the configuration.");
        }
    }

    public async Task<IReadOnlyList<SubscriptionView>> GetSubscriptionsAsync()
    {
        var items = await AppManager.Instance.SubItems() ?? [];
        return items.Select(ToSubscriptionView).ToArray();
    }

    public async Task<SubscriptionMutationResult> AddSubscriptionAsync(SubscriptionInput input)
    {
        if (!TryValidateSubscription(input, out var code, out var messageKey))
        {
            return new(false, code, messageKey, null);
        }

        var item = ToSubItem(input, null);
        var result = await _mutations.RunAsync(() => ConfigHandler.AddSubItem(Config, item));
        if (result != 0)
        {
            return new(false, "subscription_save_failed", ApiMessageKeys.SubscriptionSaveFailed, null);
        }

        var saved = (await AppManager.Instance.SubItems())?.FirstOrDefault(candidate => candidate.Url == item.Url);
        return saved is null
            ? new(false, "subscription_save_failed", ApiMessageKeys.SubscriptionSaveFailed, null)
            : new(true, "ok", ApiMessageKeys.SubscriptionAdded, ToSubscriptionView(saved));
    }

    public async Task<SubscriptionMutationResult> UpdateSubscriptionAsync(string id, SubscriptionInput input)
    {
        if (!TryValidateSubscription(input, out var code, out var messageKey))
        {
            return new(false, code, messageKey, null);
        }

        return await _mutations.RunAsync(async () =>
        {
            var existing = await AppManager.Instance.GetSubItem(id);
            if (existing is null)
            {
                return new SubscriptionMutationResult(false, "subscription_not_found", ApiMessageKeys.SubscriptionNotFound, null);
            }

            var item = ToSubItem(input, existing);
            item.Id = id;
            var result = await ConfigHandler.AddSubItem(Config, item);
            return result == 0
                ? new SubscriptionMutationResult(true, "ok", ApiMessageKeys.SubscriptionSaved, ToSubscriptionView(item))
                : new SubscriptionMutationResult(false, "subscription_save_failed", ApiMessageKeys.SubscriptionSaveFailed, null);
        });
    }

    public async Task<OperationView> DeleteSubscriptionAsync(string id)
    {
        var subscription = await AppManager.Instance.GetSubItem(id);
        if (subscription is null)
        {
            return OperationView.Fail("subscription_not_found", ApiMessageKeys.SubscriptionNotFound);
        }

        var selectedProfile = await AppManager.Instance.GetProfileItem(Config.IndexId);
        var removesCurrentProfile = selectedProfile?.Subid == id;
        if (removesCurrentProfile)
        {
            await StopCoreAsync(CancellationToken.None);
        }

        await _mutations.RunAsync(async () =>
        {
            if (await ConfigHandler.DeleteSubItem(Config, id) != 0)
            {
                throw new IOException("ServiceLib could not delete the subscription.");
            }
            if (removesCurrentProfile)
            {
                _ = await ConfigHandler.GetDefaultServer(Config);
                await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
            }
        });

        AddLog("subscription", ApiMessageKeys.SubscriptionDeleted);
        return OperationView.Ok(ApiMessageKeys.SubscriptionDeleted);
    }

    public bool StartSubscriptionUpdate(string id, bool useProxy)
    {
        return StartSubscriptionUpdateTask(id, useProxy, _operations.ShutdownToken) is not null;
    }

    private Task? StartSubscriptionUpdateTask(string id, bool useProxy, CancellationToken operationToken)
    {
        lock (_subscriptionGate)
        {
            var hasConflictingUpdate = _subscriptionTasks.Any(pair =>
                !pair.Value.IsCompleted
                && (pair.Key.Length == 0 || id.Length == 0 || pair.Key == id));
            if (hasConflictingUpdate)
            {
                return null;
            }

            var task = Task.Run(async () =>
            {
                try
                {
                    await using var operation = await _operations.EnterOperationAsync(operationToken);
                    await _mutations.RunAsync(async () =>
                    {
                        await SubscriptionHandler.UpdateProcess(Config, id, useProxy, (success, message) =>
                        {
                            var payload = new
                            {
                                subscriptionId = id,
                                success,
                                code = success ? "ok" : "subscription_update_progress",
                                messageKey = success ? ApiMessageKeys.SubscriptionSaved : ApiMessageKeys.SubscriptionUpdateProgress,
                                rawLog = message,
                            };
                            _events.Publish("subscription-progress", payload);
                            AddLog("subscription", message);
                            return Task.CompletedTask;
                        });

                        operation.Token.ThrowIfCancellationRequested();
                        await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
                        await UpdateSubscriptionTimestampLockedAsync(id);
                    }, operation.Token);
                    _events.Publish("profiles-changed", new { subscriptionId = id });
                }
                catch (OperationCanceledException) when (_operations.IsStopping || operationToken.IsCancellationRequested)
                {
                    // Expected during graceful service shutdown.
                }
                catch (Exception ex)
                {
                    AddLog("subscription", ex.Message);
                }
                finally
                {
                    lock (_subscriptionGate)
                    {
                        _subscriptionTasks.Remove(id);
                    }
                }
            });

            _subscriptionTasks[id] = task;
            return task;
        }
    }

    public async Task<IReadOnlyList<ProfileView>> GetProfilesAsync(string? subscriptionId, string? filter)
    {
        var groupId = subscriptionId ?? Config.SubIndexId ?? string.Empty;
        var profiles = await AppManager.Instance.ProfileItems(groupId) ?? [];
        var profileModels = await AppManager.Instance.ProfileModels(groupId, string.Empty) ?? [];
        var modelMap = profileModels.ToDictionary(item => item.IndexId);
        var subscriptions = (await AppManager.Instance.SubItems() ?? []).ToDictionary(item => item.Id, item => item.Remarks);
        var extensions = await ProfileExManager.Instance.GetProfileExs();
        var extensionMap = extensions.ToDictionary(item => item.IndexId);
        var statistics = (Config.GuiItem.EnableStatistics ? StatisticsManager.Instance.ServerStat : null) ?? [];
        var statisticsMap = statistics.ToDictionary(item => item.IndexId);
        var query = filter?.Trim();

        return profiles
            .Where(item => string.IsNullOrEmpty(query)
                || Utils.IsRegexMatch(item.Remarks, query)
                || Utils.IsRegexMatch(item.Address, query))
            .Select(item =>
            {
                extensionMap.TryGetValue(item.IndexId, out var extension);
                modelMap.TryGetValue(item.IndexId, out var model);
                statisticsMap.TryGetValue(item.IndexId, out var statistic);
                var delay = extension?.Delay ?? 0;
                var speed = extension?.Speed ?? 0;
                return new ProfileView(
                    item.IndexId,
                    item.Remarks,
                    System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(item.ConfigType.ToString()),
                    item.Address,
                    item.Port,
                    item.Network,
                    item.StreamSecurity,
                    item.Subid,
                    model?.SubRemarks ?? subscriptions.GetValueOrDefault(item.Subid),
                    System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(AppManager.Instance.GetCoreType(item, item.ConfigType).ToString()),
                    extension?.Sort ?? 0,
                    delay,
                    speed,
                    extension?.IpInfo,
                    statistic?.TodayUp ?? 0,
                    statistic?.TodayDown ?? 0,
                    statistic?.TotalUp ?? 0,
                    statistic?.TotalDown ?? 0,
                    item.IndexId == Config.IndexId,
                    !item.IsComplex() && item.Port > 0);
            })
            .OrderBy(item => item.Sort)
            .ToArray();
    }

    public async Task<ProfileItem?> GetProfileDetailsAsync(string profileId) =>
        await AppManager.Instance.GetProfileItem(profileId);

    public async Task<StatusView> GetStatusAsync()
    {
        var selectedProfile = await AppManager.Instance.GetProfileItem(Config.IndexId);
        var profileItems = await AppManager.Instance.ProfileItems(string.Empty) ?? [];
        var subscriptions = await AppManager.Instance.SubItems() ?? [];
        var firstInbound = Config.Inbound.FirstOrDefault();
        var port = firstInbound?.LocalPort ?? 0;
        var listeners = new List<ListenerView>();
        if (firstInbound is not null && port is > 0 and <= 65535)
        {
            var localAddress = firstInbound.AllowLANConn && !firstInbound.NewPort4LAN ? "0.0.0.0" : "127.0.0.1";
            listeners.Add(new ListenerView("local", ["http", "socks"], localAddress, port,
                await IsListeningAsync(port, CancellationToken.None)));
            if (firstInbound.SecondLocalPortEnabled && port + (int)EInboundProtocol.socks2 <= 65535)
            {
                var secondaryPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks2);
                listeners.Add(new ListenerView("local-secondary", ["http", "socks"], "127.0.0.1", secondaryPort,
                    await IsListeningAsync(secondaryPort, CancellationToken.None)));
            }
            if (firstInbound.AllowLANConn && firstInbound.NewPort4LAN
                && port + (int)EInboundProtocol.socks3 <= 65535)
            {
                var lanPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks3);
                listeners.Add(new ListenerView("lan", ["http", "socks"], "0.0.0.0", lanPort,
                    await IsListeningAsync(lanPort, CancellationToken.None)));
            }
        }
        var listening = listeners.FirstOrDefault()?.Listening ?? false;
        return new StatusView(
            _coreStartedAt is not null && listening,
            _coreStartedAt is not null && listening
                ? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(AppManager.Instance.RunningCoreType.ToString())
                : null,
            selectedProfile?.IndexId,
            selectedProfile?.Remarks,
            listening ? _coreStartedAt : null,
            listeners.ToArray(),
            !string.IsNullOrEmpty(_xrayPath),
            Utils.GetRuntimeInfo(),
            profileItems.Count,
            subscriptions.Count,
            DateTimeOffset.UtcNow,
            Config.GuiItem.EnableStatistics,
            _latestTraffic is null ? null : new TrafficView(
                _latestTraffic.ProxyUp,
                _latestTraffic.ProxyDown,
                _latestTraffic.DirectUp,
                _latestTraffic.DirectDown));
    }

    public async Task<OperationView> SelectProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        var profile = await AppManager.Instance.GetProfileItem(profileId);
        if (profile is null)
        {
            return OperationView.Fail("profile_not_found", ApiMessageKeys.ProfileNotFound);
        }

        return await StartCoreAsync(profile, cancellationToken, selectProfile: true);
    }

    public async Task<OperationView> StartCoreAsync(string? profileId, CancellationToken cancellationToken)
    {
        ProfileItem? profile = null;
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            profile = await AppManager.Instance.GetProfileItem(profileId);
            if (profile is null)
            {
                return OperationView.Fail("profile_not_found", ApiMessageKeys.ProfileNotFound);
            }
        }

        return await StartCoreAsync(profile, cancellationToken);
    }

    private async Task<OperationView> StartCoreAsync(ProfileItem? requestedProfile, CancellationToken cancellationToken, bool selectProfile = false)
    {
        await _coreGate.WaitAsync(cancellationToken);
        try
        {
            return await StartCoreLockedAsync(requestedProfile, cancellationToken, selectProfile);
        }
        finally
        {
            _coreGate.Release();
        }
    }

    private async Task<OperationView> StartCoreLockedAsync(ProfileItem? requestedProfile, CancellationToken cancellationToken, bool selectProfile = false)
    {
        var profile = requestedProfile ?? await GetDefaultProfileAsync();
        if (profile is null)
        {
            return OperationView.Fail("profile_not_selected", ApiMessageKeys.ProfileNoneSelected);
        }

        var preflight = await BuildAndValidateCoreLaunchAsync(profile);
        if (preflight.Failure is { } preflightFailure)
        {
            return preflightFailure;
        }

        if (selectProfile)
        {
            await _mutations.RunAsync(async () =>
            {
                Config.IndexId = profile.IndexId;
                await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
            });
        }

        return await LaunchPreflightedCoreLockedAsync(preflight, cancellationToken);
    }

    private async Task<ProfileItem?> GetDefaultProfileAsync() => await _mutations.RunAsync(async () =>
    {
        var selected = await ConfigHandler.GetDefaultServer(Config);
        if (selected is not null)
        {
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        }
        return selected;
    });

    private async Task<CoreLaunchPreflightResult> BuildAndValidateCoreLaunchAsync(ProfileItem profile)
    {
        var preflight = await CoreLaunchPreflight.BuildAndValidateAsync(Config, profile, coreType =>
        {
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
            var executable = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out var missingCoreMessage);
            if (!string.IsNullOrEmpty(executable))
            {
                return null;
            }

            AddLog("core", missingCoreMessage);
            return OperationView.Fail("core_binary_missing", ApiMessageKeys.CoreBinaryMissing,
                new { coreType = coreType.ToString() });
        });

        if (preflight.Failure?.Code == "profile_validation_failed")
        {
            foreach (var message in preflight.BuiltContext.CombinedValidatorResult.Errors
                         .Concat(preflight.BuiltContext.CombinedValidatorResult.Warnings))
            {
                AddLog("core-validation", message);
            }
        }

        return preflight;
    }

    private async Task<OperationView> LaunchPreflightedCoreLockedAsync(
        CoreLaunchPreflightResult preflight,
        CancellationToken cancellationToken)
    {
        var profile = preflight.Profile;
        var built = preflight.BuiltContext;
        var port = Config.Inbound.FirstOrDefault()?.LocalPort ?? 0;
        if (_coreStartedAt is null && await IsListeningAsync(port, cancellationToken))
        {
            return OperationView.Fail("proxy_port_in_use", ApiMessageKeys.CorePortInUse, new { port });
        }

        await CoreManager.Instance.LoadCore(built.MainResult.Context, built.PreSocksResult?.Context);
        var started = await WaitForListenerAsync(port, cancellationToken);
        _coreStartedAt = started ? DateTimeOffset.UtcNow : null;
        var operation = started
            ? OperationView.Ok(ApiMessageKeys.CoreStarted, new { profileId = profile.IndexId })
            : OperationView.Fail("core_start_failed", ApiMessageKeys.CoreStartFailed, new { profileId = profile.IndexId });
        AddLog("core", operation.MessageKey);
        _events.Publish("status", await GetStatusAsync());
        return operation;
    }

    internal static OperationView? GetTunLaunchRejection(CoreConfigContextBuilderAllResult built) =>
        CoreLaunchPreflight.GetTunLaunchRejection(built);

    public async Task<OperationView> StopCoreAsync(CancellationToken cancellationToken)
    {
        await _coreGate.WaitAsync(cancellationToken);
        try
        {
            await CoreManager.Instance.CoreStop();
            _coreStartedAt = null;
            AddLog("core", ApiMessageKeys.CoreStopped);
            _events.Publish("status", await GetStatusAsync());
            return OperationView.Ok(ApiMessageKeys.CoreStopped);
        }
        finally
        {
            _coreGate.Release();
        }
    }

    public async Task<OperationView> RestartCoreAsync(CancellationToken cancellationToken)
    {
        await _coreGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await GetDefaultProfileAsync();
            if (profile is null)
            {
                return OperationView.Fail("profile_not_selected", ApiMessageKeys.ProfileNoneSelected);
            }

            return await CoreRestartFlow.ExecuteAsync(
                () => BuildAndValidateCoreLaunchAsync(profile),
                () => CoreManager.Instance.CoreStop(),
                () => _coreStartedAt = null,
                preflight => LaunchPreflightedCoreLockedAsync(preflight, cancellationToken));
        }
        finally
        {
            _coreGate.Release();
        }
    }

    public async Task<OperationView> StartLatencyTestAsync(string profileId)
    {
        var profile = await AppManager.Instance.GetProfileItem(profileId);
        if (profile is null || profile.IsComplex() || profile.Port <= 0)
        {
            return OperationView.Fail("speedtest_invalid_profile", ApiMessageKeys.SpeedTestInvalidProfile);
        }

        return await StartSpeedTestAsync(new SpeedTestRequest(ESpeedActionType.Tcping, [profileId]), CancellationToken.None);
    }

    public OperationView StopLatencyTests()
    {
        _speedtestService?.ExitLoop();
        return OperationView.Ok(ApiMessageKeys.CommonCompleted);
    }

    public IReadOnlyList<LogView> GetRecentLogs(int limit, string? filter = null) => _logs.Recent(limit, filter);

    public LogPageView GetRecentLogsPage(int page, int pageSize, string? filter = null) => _logs.RecentPage(page, pageSize, filter);

    public void ClearLogs() => _logs.Clear();

    private async Task OnCoreMessageAsync(bool notify, string message)
    {
        AddLog("core", message);
        await Task.CompletedTask;
    }

    private void AddLog(string source, string message)
    {
        var entry = _logs.Add(source, message);
        _events.Publish("log", entry);
        Console.Out.WriteLine($"[{source}] {message.TrimEnd()}");
    }

    private static void ConfigureServiceLibConsoleLogging()
    {
        var logging = LogManager.Configuration;
        if (logging is null)
        {
            return;
        }

        var target = new ConsoleTarget("web-console")
        {
            Layout = "${longdate}|${level:uppercase=true}|${logger}|${message}",
        };
        logging.AddTarget(target);
        logging.LoggingRules.Add(new LoggingRule("*", NLog.LogLevel.Debug, target));
        LogManager.Configuration = logging;
    }

    private Task OnStatisticsUpdateAsync(ServerSpeedItem update)
    {
        _latestTraffic = update;
        _events.Publish("traffic", update);
        return Task.CompletedTask;
    }

    // The subscription update owns the mutation gate while calling this helper.
    private async Task UpdateSubscriptionTimestampLockedAsync(string subscriptionId)
    {
        var updateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (subscriptionId.Length > 0)
        {
            var item = await AppManager.Instance.GetSubItem(subscriptionId);
            if (item is not null)
            {
                item.UpdateTime = updateTime;
                if (await ConfigHandler.AddSubItem(Config, item) != 0)
                {
                    throw new IOException("ServiceLib could not save the subscription timestamp.");
                }
            }
            return;
        }

        foreach (var item in await AppManager.Instance.SubItems() ?? [])
        {
            if (item.Enabled)
            {
                item.UpdateTime = updateTime;
                if (await ConfigHandler.AddSubItem(Config, item) != 0)
                {
                    throw new IOException("ServiceLib could not save the subscription timestamp.");
                }
            }
        }
    }

    private static bool TryValidateSubscription(SubscriptionInput input, out string code, out string messageKey)
    {
        if (string.IsNullOrWhiteSpace(input.Remarks))
        {
            code = "subscription_name_required";
            messageKey = ApiMessageKeys.SubscriptionNameRequired;
            return false;
        }
        if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            code = "subscription_url_invalid";
            messageKey = ApiMessageKeys.SubscriptionInvalidUrl;
            return false;
        }

        code = "ok";
        messageKey = string.Empty;
        return true;
    }

    private static SubItem ToSubItem(SubscriptionInput input, SubItem? existing) => new()
    {
        Id = existing?.Id ?? string.Empty,
        Remarks = input.Remarks.Trim(),
        Url = input.Url.Trim(),
        MoreUrl = input.MoreUrl?.Trim() ?? existing?.MoreUrl ?? string.Empty,
        Enabled = input.Enabled ?? existing?.Enabled ?? true,
        UserAgent = input.UserAgent?.Trim() ?? existing?.UserAgent ?? string.Empty,
        RequestHeaders = input.RequestHeaders ?? existing?.RequestHeaders,
        Filter = input.Filter ?? existing?.Filter,
        AutoUpdateInterval = Math.Max(input.AutoUpdateInterval ?? existing?.AutoUpdateInterval ?? 0, 0),
        ConvertTarget = input.ConvertTarget ?? existing?.ConvertTarget,
        Memo = input.Memo ?? existing?.Memo,
        Sort = input.Sort ?? existing?.Sort ?? 0,
        UpdateTime = existing?.UpdateTime ?? 0,
        PrevProfile = input.PrevProfile ?? existing?.PrevProfile,
        NextProfile = input.NextProfile ?? existing?.NextProfile,
        PreSocksPort = input.PreSocksPort ?? existing?.PreSocksPort,
        CustomCoreType = input.CustomCoreType ?? existing?.CustomCoreType,
    };

    private static SubscriptionView ToSubscriptionView(SubItem item) => new(
        item.Id,
        item.Remarks,
        item.Url,
        item.Enabled,
        item.MoreUrl,
        item.Filter,
        item.AutoUpdateInterval,
        item.UpdateTime,
        item.UserAgent,
        item.RequestHeaders,
        item.ConvertTarget,
        item.Memo,
        item.Sort,
        item.PrevProfile,
        item.NextProfile,
        item.PreSocksPort,
        item.CustomCoreType is null ? null : System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(item.CustomCoreType.Value.ToString()));

    private static void EnsureInboundDefaults(Config config)
    {
        if (config.Inbound.Count == 0)
        {
            config.Inbound.Add(new InItem
            {
                Protocol = nameof(EInboundProtocol.socks),
                LocalPort = 10808,
                UdpEnabled = true,
                SniffingEnabled = true,
            });
        }

        if (config.Inbound[0].LocalPort is <= 0 or > 65535)
        {
            config.Inbound[0].LocalPort = 10808;
        }
    }

    private static string? FindXrayExecutable(out string message)
    {
        var info = CoreInfoManager.Instance.GetCoreInfo(ECoreType.Xray);
        foreach (var name in info?.CoreExes ?? [])
        {
            var path = Utils.GetBinPath(Utils.GetExeName(name), ECoreType.Xray.ToString());
            if (File.Exists(path))
            {
                message = string.Empty;
                return path;
            }
        }

        message = $"Xray-core executable was not found under {Utils.GetBinPath(string.Empty, ECoreType.Xray.ToString())}.";
        return null;
    }

    private static async Task<bool> WaitForListenerAsync(int port, CancellationToken cancellationToken)
    {
        if (port is <= 0 or > 65535)
        {
            return false;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsListeningAsync(port, cancellationToken))
            {
                return true;
            }
            await Task.Delay(100, cancellationToken);
        }

        return false;
    }

    private static async Task<bool> IsListeningAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

}
