using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Entities;
using v2rayN.Web.Contracts;

namespace v2rayN.Web.Services;

public sealed partial class V2rayRuntime
{
    public Task<WebSettingsView> GetSettingsAsync()
    {
        var inbound = Config.Inbound[0];
        return Task.FromResult(new WebSettingsView(
            new InboundSettingsView(
                inbound.LocalPort,
                inbound.SecondLocalPortEnabled,
                inbound.UdpEnabled,
                inbound.SniffingEnabled,
                inbound.DestOverride?.ToArray() ?? [],
                inbound.RouteOnly,
                inbound.AllowLANConn,
                inbound.NewPort4LAN,
                inbound.User,
                inbound.Pass),
            GetTunSettings(),
            new CoreSettingsView(
                Config.CoreBasicItem.LogEnabled,
                Config.CoreBasicItem.Loglevel,
                Config.CoreBasicItem.DefFingerprint,
                Config.CoreBasicItem.DefUserAgent,
                Config.CoreBasicItem.SendThrough,
                Config.CoreBasicItem.BindInterface,
                Config.Mux4RayItem.Concurrency,
                Config.Mux4RayItem.XudpConcurrency,
                Config.Mux4RayItem.XudpProxyUDP443,
                Config.Mux4SboxItem.Protocol,
                Config.Mux4SboxItem.MaxConnections,
                Config.Mux4SboxItem.Padding,
                Config.CoreBasicItem.EnableCacheFile4Sbox,
                Config.HysteriaItem.UpMbps,
                Config.HysteriaItem.DownMbps,
                Config.CoreBasicItem.EnableFragment,
                Config.CoreBasicItem.EnableFinalFragment,
                Config.Fragment4RayItem?.Packets,
                Config.Fragment4RayItem?.Lengths?.ToArray() ?? [],
                Config.Fragment4RayItem?.Delays?.ToArray() ?? [],
                Config.Fragment4RayItem?.MaxSplit),
            new AppSettingsView(
                Config.GuiItem.EnableStatistics,
                Config.GuiItem.DisplayRealTimeSpeed,
                Config.GuiItem.KeepOlderDedupl,
                Config.GuiItem.AutoUpdateInterval,
                Config.GuiItem.RootCertProvider,
                Config.ConstItem.GeoSourceUrl,
                Config.ConstItem.SrsSourceUrl,
                Config.ConstItem.RouteRulesTemplateSourceUrl,
                Config.ConstItem.SubConvertUrl),
            new SpeedTestSettingsView(
                Config.SpeedTestItem.SpeedTestTimeout,
                Config.SpeedTestItem.SpeedTestUrl,
                Config.SpeedTestItem.SpeedPingTestUrl,
                Config.SpeedTestItem.MixedConcurrencyCount,
                Config.SpeedTestItem.IPAPIUrl,
                Config.SpeedTestItem.UdpTestTarget,
                Config.SpeedTestItem.SpeedTestPageSize,
                Config.SpeedTestItem.SpeedTestDelayInterval),
            Config.RoutingBasicItem.DomainStrategy,
            Config.RoutingBasicItem.DomainStrategy4Singbox,
            Config.CoreTypeItem.Select(item => new CoreTypeMapping(item.ConfigType, item.CoreType)).ToArray()));
    }

    public async Task<OperationView> UpdateInboundSettingsAsync(InboundSettingsInput input)
    {
        var highestPortOffset = input.SecondLocalPortEnabled || (input.AllowLANConn && input.NewPort4LAN) ? 2 : 0;
        if (input.LocalPort <= 0 || input.LocalPort > 65535 - highestPortOffset)
        {
            return OperationView.Fail("inbound_port_invalid", ApiMessageKeys.SettingsInvalidPort);
        }

        await _mutations.RunAsync(async () =>
        {
            var inbound = Config.Inbound[0];
            inbound.LocalPort = input.LocalPort;
            inbound.SecondLocalPortEnabled = input.SecondLocalPortEnabled;
            inbound.UdpEnabled = input.UdpEnabled;
            inbound.SniffingEnabled = input.SniffingEnabled;
            inbound.DestOverride = input.DestOverride?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
            inbound.RouteOnly = input.RouteOnly;
            inbound.AllowLANConn = input.AllowLANConn;
            inbound.NewPort4LAN = input.AllowLANConn && input.NewPort4LAN;
            inbound.User = input.User?.Trim() ?? string.Empty;
            inbound.Pass = input.Pass?.Trim() ?? string.Empty;
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        });

        var restartRequired = _coreStartedAt is not null;
        _events.Publish("settings-changed", new { section = "inbound", restartRequired });
        return OperationView.Ok(restartRequired ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved, new { restartRequired });
    }

    public TunSettingsView GetTunSettings()
    {
        var tun = Config.TunModeItem;
        var capability = GetTunCapability();
        return new TunSettingsView(
            tun.EnableTun,
            tun.AutoRoute,
            tun.StrictRoute,
            tun.Stack,
            tun.Mtu,
            tun.EnableIPv6Address,
            tun.IcmpRouting,
            tun.EnableLegacyProtect,
            tun.RouteExcludeAddress?.ToArray() ?? [],
            tun.IPv4Address,
            tun.IPv6Address,
            capability.Available,
            capability.MessageKey);
    }

    public async Task<OperationView> UpdateTunSettingsAsync(TunSettingsInput input)
    {
        var capability = GetTunCapability();
        if (input.Enabled && !capability.Available)
        {
            return OperationView.Fail("tun_capability_unavailable", capability.MessageKey ?? "tun.capabilityUnavailable");
        }
        if (input.Mtu is < 0 or > 65535)
        {
            return OperationView.Fail("tun_mtu_invalid", ApiMessageKeys.CommonInvalidInput);
        }

        TunModeItem? oldTun = null;
        var wasRunning = _coreStartedAt is not null;
        await _mutations.RunAsync(async () =>
        {
            var current = Config.TunModeItem;
            oldTun = new TunModeItem
            {
                EnableTun = current.EnableTun,
                AutoRoute = current.AutoRoute,
                StrictRoute = current.StrictRoute,
                Stack = current.Stack,
                Mtu = current.Mtu,
                EnableIPv6Address = current.EnableIPv6Address,
                IcmpRouting = current.IcmpRouting,
                EnableLegacyProtect = current.EnableLegacyProtect,
                RouteExcludeAddress = current.RouteExcludeAddress?.ToList(),
                IPv4Address = current.IPv4Address,
                IPv6Address = current.IPv6Address,
            };
            Config.TunModeItem = new TunModeItem
            {
                EnableTun = input.Enabled,
                AutoRoute = input.AutoRoute,
                StrictRoute = input.StrictRoute,
                Stack = input.Stack?.Trim() ?? string.Empty,
                Mtu = input.Mtu,
                EnableIPv6Address = input.EnableIPv6Address,
                IcmpRouting = input.IcmpRouting?.Trim() ?? string.Empty,
                EnableLegacyProtect = input.EnableLegacyProtect,
                RouteExcludeAddress = input.RouteExcludeAddress?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToList() ?? [],
                IPv4Address = input.IPv4Address?.Trim() ?? string.Empty,
                IPv6Address = input.IPv6Address?.Trim() ?? string.Empty,
            };
            try
            {
                await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
            }
            catch
            {
                Config.TunModeItem = oldTun;
                throw;
            }
        });

        if (wasRunning)
        {
            var restart = await RestartCoreAsync(CancellationToken.None);
            if (!restart.Success)
            {
                await _mutations.RunAsync(async () =>
                {
                    Config.TunModeItem = oldTun!;
                    await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
                });
                await RestartCoreAsync(CancellationToken.None);
                return restart;
            }
        }

        _events.Publish("settings-changed", new { section = "tun", restartRequired = false });
        return OperationView.Ok(ApiMessageKeys.CommonSaved, new { enabled = input.Enabled, coreRestarted = wasRunning });
    }

    private static (bool Available, string? MessageKey) GetTunCapability()
    {
        if (!OperatingSystem.IsLinux())
        {
            return (false, "tun.unsupportedPlatform");
        }
        if (!File.Exists("/dev/net/tun"))
        {
            return (false, "tun.deviceUnavailable");
        }

        try
        {
            using var device = new FileStream("/dev/net/tun", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (CoreManager.CanRunTunCoreWithoutSudo())
            {
                return (true, null);
            }
            return (false, "tun.netAdminRequired");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "tun.devicePermissionDenied");
        }
        catch (IOException)
        {
            return (false, "tun.deviceUnavailable");
        }
    }

    public async Task<OperationView> UpdateCoreSettingsAsync(CoreSettingsInput input)
    {
        if (!Global.LogLevels.Contains(input.Loglevel ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return OperationView.Fail("core_log_level_invalid", ApiMessageKeys.SettingsInvalidCoreLogLevel);
        }
        if (input.Mux4RayConcurrency is < 0 or > 1024 || input.Mux4RayXudpConcurrency is < 0 or > 1024
            || input.Mux4SboxMaxConnections is < 0 or > 1024 || input.Hy2UpMbps < 0 || input.Hy2DownMbps < 0)
        {
            return OperationView.Fail("core_setting_out_of_range", ApiMessageKeys.SettingsInvalidCoreValue);
        }
        var fragmentLengths = input.FragmentLengths ?? [];
        var fragmentDelays = input.FragmentDelays ?? [];
        if (fragmentLengths.Any(value => !Utils.TryParseRange(value, 0, int.MaxValue, out _, out _))
            || fragmentDelays.Any(value => !Utils.TryParseRange(value, 0, int.MaxValue, out _, out _))
            || (!string.IsNullOrWhiteSpace(input.FragmentMaxSplit)
                && !Utils.TryParseMaxSplit(input.FragmentMaxSplit, 0, 10000, out _, out _)))
        {
            return OperationView.Fail("fragment_setting_invalid", ApiMessageKeys.SettingsInvalidFragment);
        }

        await _mutations.RunAsync(async () =>
        {
            Config.CoreBasicItem.LogEnabled = input.LogEnabled;
            Config.CoreBasicItem.Loglevel = input.Loglevel ?? string.Empty;
            Config.CoreBasicItem.DefFingerprint = input.DefFingerprint?.Trim() ?? string.Empty;
            Config.CoreBasicItem.DefUserAgent = input.DefUserAgent?.Trim() ?? string.Empty;
            Config.CoreBasicItem.SendThrough = input.SendThrough?.Trim();
            Config.CoreBasicItem.BindInterface = input.BindInterface?.Trim();
            Config.Mux4RayItem.Concurrency = input.Mux4RayConcurrency is > 0 ? input.Mux4RayConcurrency : null;
            Config.Mux4RayItem.XudpConcurrency = input.Mux4RayXudpConcurrency is > 0 ? input.Mux4RayXudpConcurrency : null;
            Config.Mux4RayItem.XudpProxyUDP443 = input.Mux4RayXudpProxyUDP443;
            Config.Mux4SboxItem.Protocol = input.Mux4SboxProtocol ?? string.Empty;
            Config.Mux4SboxItem.MaxConnections = input.Mux4SboxMaxConnections;
            Config.Mux4SboxItem.Padding = input.Mux4SboxPadding;
            Config.CoreBasicItem.EnableCacheFile4Sbox = input.EnableCacheFile4Sbox;
            Config.HysteriaItem.UpMbps = input.Hy2UpMbps;
            Config.HysteriaItem.DownMbps = input.Hy2DownMbps;
            Config.CoreBasicItem.EnableFragment = input.EnableFragment;
            Config.CoreBasicItem.EnableFinalFragment = input.EnableFinalFragment;
            Config.Fragment4RayItem ??= new();
            Config.Fragment4RayItem.Packets = input.FragmentPackets;
            Config.Fragment4RayItem.Lengths = fragmentLengths.ToList();
            Config.Fragment4RayItem.Delays = fragmentDelays.ToList();
            Config.Fragment4RayItem.MaxSplit = input.FragmentMaxSplit;
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        });

        var restartRequired = _coreStartedAt is not null;
        _events.Publish("settings-changed", new { section = "core", restartRequired });
        return OperationView.Ok(restartRequired ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved, new { restartRequired });
    }

    public async Task<OperationView> UpdateAppSettingsAsync(AppSettingsInput input)
    {
        var statisticsChanged = Config.GuiItem.EnableStatistics != input.EnableStatistics
            || Config.GuiItem.DisplayRealTimeSpeed != input.DisplayRealTimeSpeed;

        await _mutations.RunAsync(async () =>
        {
            Config.GuiItem.EnableStatistics = input.EnableStatistics;
            Config.GuiItem.DisplayRealTimeSpeed = input.DisplayRealTimeSpeed;
            Config.GuiItem.KeepOlderDedupl = input.KeepOlderDedupl;
            Config.GuiItem.AutoUpdateInterval = Math.Max(0, input.GeoAutoUpdateInterval);
            Config.GuiItem.RootCertProvider = input.RootCertProvider;
            Config.ConstItem.GeoSourceUrl = input.GeoSourceUrl;
            Config.ConstItem.SrsSourceUrl = input.SrsSourceUrl;
            Config.ConstItem.RouteRulesTemplateSourceUrl = input.RouteRulesTemplateSourceUrl;
            Config.ConstItem.SubConvertUrl = input.SubConvertUrl;
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        });

        if (statisticsChanged)
        {
            StatisticsManager.Instance.Close();
            _latestTraffic = null;
            if (Config.GuiItem.EnableStatistics || Config.GuiItem.DisplayRealTimeSpeed)
            {
                await StatisticsManager.Instance.Init(Config, OnStatisticsUpdateAsync);
            }
        }

        _events.Publish("settings-changed", new { section = "application", restartRequired = false });
        return OperationView.Ok(ApiMessageKeys.CommonSaved);
    }

    public async Task<OperationView> UpdateSpeedTestSettingsAsync(SpeedTestSettingsInput input)
    {
        if (input.SpeedTestTimeout <= 0 || input.MixedConcurrencyCount <= 0
            || input.SpeedTestPageSize is <= 0 || input.SpeedTestDelayInterval is < 0
            || !IsHttpUrl(input.SpeedTestUrl) || !IsHttpUrl(input.SpeedPingTestUrl))
        {
            return OperationView.Fail("speedtest_settings_invalid", ApiMessageKeys.SettingsInvalidSpeedTest);
        }

        await _mutations.RunAsync(async () =>
        {
            Config.SpeedTestItem.SpeedTestTimeout = input.SpeedTestTimeout;
            Config.SpeedTestItem.SpeedTestUrl = input.SpeedTestUrl;
            Config.SpeedTestItem.SpeedPingTestUrl = input.SpeedPingTestUrl;
            Config.SpeedTestItem.MixedConcurrencyCount = Math.Max(input.MixedConcurrencyCount, Global.SpeedTestConcurrencyCountMin);
            Config.SpeedTestItem.IPAPIUrl = input.IPAPIUrl;
            Config.SpeedTestItem.UdpTestTarget = input.UdpTestTarget;
            Config.SpeedTestItem.SpeedTestPageSize = input.SpeedTestPageSize;
            Config.SpeedTestItem.SpeedTestDelayInterval = input.SpeedTestDelayInterval;
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        });
        _events.Publish("settings-changed", new { section = "speedtest", restartRequired = false });
        return OperationView.Ok(ApiMessageKeys.SpeedTestSettingsSaved);
    }

    public async Task<OperationView> UpdateCoreTypeMappingsAsync(IEnumerable<CoreTypeMapping> mappings)
    {
        await _mutations.RunAsync(async () =>
        {
            EnsureCoreTypeMappings();
            foreach (var mapping in mappings)
            {
                var item = Config.CoreTypeItem.FirstOrDefault(entry => entry.ConfigType == mapping.ConfigType);
                if (item is not null)
                {
                    item.CoreType = mapping.CoreType;
                }
            }
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        });
        var restartRequired = _coreStartedAt is not null;
        _events.Publish("settings-changed", new { section = "core-types", restartRequired });
        return OperationView.Ok(restartRequired ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved, new { restartRequired });
    }

    private void EnsureCoreTypeMappings()
    {
        Config.CoreTypeItem ??= [];
        foreach (var configType in Enum.GetValues<EConfigType>())
        {
            if (Config.CoreTypeItem.All(item => item.ConfigType != configType))
            {
                Config.CoreTypeItem.Add(new CoreTypeItem { ConfigType = configType, CoreType = ECoreType.Xray });
            }
        }
    }

    public async Task<OperationView> UpdateRoutingStrategiesAsync(RoutingSettingsInput input)
    {
        if (!Global.DomainStrategies.Contains(input.DomainStrategy ?? string.Empty)
            || !Global.DomainStrategies4Sbox.Contains(input.DomainStrategy4Singbox ?? string.Empty))
        {
            return OperationView.Fail("routing_strategy_invalid", ApiMessageKeys.SettingsInvalidRoutingStrategy);
        }

        await _mutations.RunAsync(async () =>
        {
            Config.RoutingBasicItem.DomainStrategy = input.DomainStrategy!;
            Config.RoutingBasicItem.DomainStrategy4Singbox = input.DomainStrategy4Singbox!;
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        });
        _events.Publish("settings-changed", new { section = "routing", restartRequired = _coreStartedAt is not null });
        return OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved, new { restartRequired = _coreStartedAt is not null });
    }

    public Task<SimpleDNSItem> GetSimpleDNSAsync() => Task.FromResult(Config.SimpleDNSItem);

    public async Task<OperationView> UpdateSimpleDNSAsync(SimpleDNSItem input)
    {
        await _mutations.RunAsync(async () =>
        {
            Config.SimpleDNSItem = input;
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        });
        _events.Publish("settings-changed", new { section = "dns", restartRequired = _coreStartedAt is not null });
        return OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved, new { restartRequired = _coreStartedAt is not null });
    }

    public async Task<IReadOnlyList<DnsProfileView>> GetDnsProfilesAsync() =>
        (await AppManager.Instance.DNSItems() ?? [])
            .Select(item => new DnsProfileView(item.Id, item.Remarks, item.Enabled, item.CoreType, item.UseSystemHosts,
                item.NormalDNS, item.TunDNS, item.DomainStrategy4Freedom, item.DomainDNSAddress))
            .ToArray();

    public async Task<OperationView> UpdateDnsProfileAsync(ECoreType coreType, DnsProfileInput input)
    {
        var result = await _mutations.RunAsync(async () =>
        {
            var current = await AppManager.Instance.GetDNSItem(coreType);
            if (current is null)
            {
                return -2;
            }
            var item = new DNSItem
            {
                Id = current.Id,
                Remarks = input.Remarks ?? current.Remarks,
                Enabled = input.Enabled,
                CoreType = coreType,
                UseSystemHosts = input.UseSystemHosts,
                NormalDNS = input.NormalDNS,
                TunDNS = input.TunDNS,
                DomainStrategy4Freedom = input.DomainStrategy4Freedom,
                DomainDNSAddress = input.DomainDNSAddress,
            };
            return await ConfigHandler.SaveDNSItems(Config, item);
        });
        if (result == -2)
        {
            return OperationView.Fail("dns_profile_not_found", ApiMessageKeys.DnsProfileNotFound, new { coreType = coreType.ToString() });
        }
        if (result != 0)
        {
            return OperationView.Fail("dns_profile_save_failed", ApiMessageKeys.DnsSaveFailed);
        }
        _events.Publish("settings-changed", new { section = "dns", coreType = coreType.ToString(), restartRequired = _coreStartedAt is not null });
        return OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved,
            new { coreType = coreType.ToString(), restartRequired = _coreStartedAt is not null });
    }

    public async Task<IReadOnlyList<RoutingItem>> GetRoutingProfilesAsync() => await AppManager.Instance.RoutingItems() ?? [];

    public async Task<OperationView> SaveRoutingProfileAsync(RoutingItem input, string? routingId = null)
    {
        if (string.IsNullOrWhiteSpace(input.Remarks))
        {
            return OperationView.Fail("routing_name_required", ApiMessageKeys.RoutingNameRequired);
        }

        var ruleJson = string.IsNullOrWhiteSpace(input.RuleSet) ? "[]" : input.RuleSet;
        var parsedRules = JsonUtils.Deserialize<List<RulesItem>>(ruleJson);
        if (parsedRules is null)
        {
            return OperationView.Fail("routing_rules_json_invalid", ApiMessageKeys.RoutingRulesInvalid);
        }
        var save = await _mutations.RunAsync(async () =>
        {
            RoutingItem item;
            if (string.IsNullOrEmpty(routingId))
            {
                item = input;
                item.Id = string.Empty;
                item.IsActive = false;
                var existing = await AppManager.Instance.RoutingItems() ?? [];
                item.Sort = item.Sort > 0 ? item.Sort : (existing.MaxBy(route => route.Sort)?.Sort ?? 0) + 1;
            }
            else
            {
                var existingItem = await AppManager.Instance.GetRoutingItem(routingId);
                if (existingItem is null)
                {
                    return (Result: -2, Item: (RoutingItem?)null);
                }
                item = existingItem;
                item.Remarks = input.Remarks;
                item.Url = input.Url;
                item.Enabled = input.Enabled;
                item.Locked = input.Locked;
                item.CustomIcon = input.CustomIcon;
                item.CustomRulesetPath4Singbox = input.CustomRulesetPath4Singbox;
                item.DomainStrategy = input.DomainStrategy;
                item.DomainStrategy4Singbox = input.DomainStrategy4Singbox;
            }

            if (parsedRules.Count == 0)
            {
                item.RuleSet = "[]";
                item.RuleNum = 0;
            }
            else
            {
                item.RuleNum = parsedRules.Count;
            }
            var result = parsedRules.Count == 0
                ? await ConfigHandler.SaveRoutingItem(Config, item)
                : await ConfigHandler.AddBatchRoutingRules(item, JsonUtils.Serialize(parsedRules, false));
            return (Result: result, Item: (RoutingItem?)item);
        });
        if (save.Result == -2)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        if (save.Result != 0 || save.Item is null)
        {
            return OperationView.Fail("routing_profile_save_failed", ApiMessageKeys.RoutingSaveFailed);
        }

        _events.Publish("settings-changed", new { section = "routing-profiles", restartRequired = _coreStartedAt is not null });
        return OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved,
            new { routingId = save.Item.Id, restartRequired = _coreStartedAt is not null });
    }

    public async Task<OperationView> DeleteRoutingProfileAsync(string id)
    {
        var removed = await _mutations.RunAsync(async () =>
        {
            var item = await AppManager.Instance.GetRoutingItem(id);
            if (item is null)
            {
                return false;
            }
            await ConfigHandler.RemoveRoutingItem(item);
            if (item.IsActive)
            {
                var remaining = await AppManager.Instance.RoutingItems() ?? [];
                var fallback = remaining.FirstOrDefault();
                if (fallback is not null)
                {
                    if (await ConfigHandler.SetDefaultRouting(Config, fallback) != 0)
                    {
                        throw new IOException("ServiceLib could not select the fallback routing profile.");
                    }
                }
                else
                {
                    Config.RoutingBasicItem.RoutingIndexId = string.Empty;
                    await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
                }
            }
            return true;
        });
        if (!removed)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        _events.Publish("settings-changed", new { section = "routing-profiles", restartRequired = _coreStartedAt is not null });
        return OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonDeleted,
            new { routingId = id, restartRequired = _coreStartedAt is not null });
    }

    public async Task<OperationView> ActivateRoutingProfileAsync(string id)
    {
        var result = await _mutations.RunAsync(async () =>
        {
            var item = await AppManager.Instance.GetRoutingItem(id);
            if (item is null)
            {
                return -2;
            }
            return item.IsActive ? 0 : await ConfigHandler.SetDefaultRouting(Config, item);
        });
        if (result == -2)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        if (result != 0)
        {
            return OperationView.Fail("routing_profile_activate_failed", ApiMessageKeys.RoutingSaveFailed);
        }
        _events.Publish("settings-changed", new { section = "routing-profiles", restartRequired = _coreStartedAt is not null });
        return OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonCompleted,
            new { routingId = id, restartRequired = _coreStartedAt is not null });
    }

    public async Task<IReadOnlyList<RulesItem>> GetRoutingRulesAsync(string routingId)
    {
        var item = await AppManager.Instance.GetRoutingItem(routingId);
        return JsonUtils.Deserialize<List<RulesItem>>(item?.RuleSet ?? "[]") ?? [];
    }

    public async Task<OperationView> SaveRoutingRulesAsync(string routingId, IEnumerable<RulesItem> rules)
    {
        var saved = await _mutations.RunAsync(() => SaveRoutingRulesLockedAsync(routingId, rules.ToArray()));
        if (saved == -2)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        if (saved != 0)
        {
            return OperationView.Fail("routing_rules_save_failed", ApiMessageKeys.RoutingSaveFailed);
        }
        _events.Publish("settings-changed", new { section = "routing-rules", routingId, restartRequired = _coreStartedAt is not null });
        return OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved,
            new { routingId, restartRequired = _coreStartedAt is not null });
    }

    private static async Task<int> SaveRoutingRulesLockedAsync(string routingId, IReadOnlyList<RulesItem> rules)
    {
        var item = await AppManager.Instance.GetRoutingItem(routingId);
        if (item is null)
        {
            return -2;
        }
        return await ConfigHandler.AddBatchRoutingRules(item, JsonUtils.Serialize(rules, false));
    }

    public async Task<OperationView> ImportRoutingRulesAsync(string routingId, RouteRulesImportInput request)
    {
        var incoming = JsonUtils.Deserialize<List<RulesItem>>(request.Content);
        if (incoming is null)
        {
            return OperationView.Fail("routing_rules_json_invalid", ApiMessageKeys.RoutingRulesInvalid);
        }
        var saved = await _mutations.RunAsync(async () =>
        {
            if (request.Append)
            {
                var item = await AppManager.Instance.GetRoutingItem(routingId);
                if (item is null)
                {
                    return -2;
                }
                var existing = JsonUtils.Deserialize<List<RulesItem>>(item.RuleSet ?? "[]") ?? [];
                existing.AddRange(incoming);
                incoming = existing;
            }
            return await SaveRoutingRulesLockedAsync(routingId, incoming);
        });
        return saved switch
        {
            -2 => OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound),
            0 => RoutingRulesSaved(routingId),
            _ => OperationView.Fail("routing_rules_save_failed", ApiMessageKeys.RoutingSaveFailed),
        };
    }

    private OperationView RoutingRulesSaved(string routingId)
    {
        _events.Publish("settings-changed", new { section = "routing-rules", routingId, restartRequired = _coreStartedAt is not null });
        return OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved,
            new { routingId, restartRequired = _coreStartedAt is not null });
    }

    public async Task<OperationView> MoveRoutingRuleAsync(string routingId, string ruleId, EMove direction, int position)
    {
        var result = await _mutations.RunAsync(async () =>
        {
            var item = await AppManager.Instance.GetRoutingItem(routingId);
            if (item is null)
            {
                return -2;
            }
            var rules = JsonUtils.Deserialize<List<RulesItem>>(item.RuleSet ?? "[]") ?? [];
            var index = rules.FindIndex(rule => rule.Id == ruleId);
            if (index < 0)
            {
                return -3;
            }
            if (await ConfigHandler.MoveRoutingRule(rules, index, direction, position) != 0)
            {
                return -4;
            }
            return await SaveRoutingRulesLockedAsync(routingId, rules);
        });
        return result switch
        {
            -2 => OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound),
            -3 => OperationView.Fail("routing_rule_not_found", ApiMessageKeys.RoutingRuleNotFound),
            -4 => OperationView.Fail("routing_rule_move_failed", ApiMessageKeys.CommonInvalidInput),
            0 => RoutingRulesSaved(routingId),
            _ => OperationView.Fail("routing_rules_save_failed", ApiMessageKeys.RoutingSaveFailed),
        };
    }

    public async Task<OperationView> DeleteRoutingRuleAsync(string routingId, string ruleId)
    {
        var result = await _mutations.RunAsync(async () =>
        {
            var item = await AppManager.Instance.GetRoutingItem(routingId);
            if (item is null)
            {
                return -2;
            }
            var rules = JsonUtils.Deserialize<List<RulesItem>>(item.RuleSet ?? "[]") ?? [];
            if (rules.RemoveAll(rule => rule.Id == ruleId) == 0)
            {
                return -3;
            }
            return await SaveRoutingRulesLockedAsync(routingId, rules);
        });
        return result switch
        {
            -2 => OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound),
            -3 => OperationView.Fail("routing_rule_not_found", ApiMessageKeys.RoutingRuleNotFound),
            0 => RoutingRulesSaved(routingId),
            _ => OperationView.Fail("routing_rules_save_failed", ApiMessageKeys.RoutingSaveFailed),
        };
    }

    public async Task<OperationView> ImportRoutingProfilesAsync()
    {
        var result = await ConfigHandler.InitRouting(Config, true, mutation => _mutations.RunAsync(mutation));
        _events.Publish("settings-changed", new { section = "routing-profiles", restartRequired = _coreStartedAt is not null });
        return result == 0
            ? OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonCompleted,
                new { restartRequired = _coreStartedAt is not null })
            : OperationView.Fail("routing_import_failed", ApiMessageKeys.RoutingSaveFailed);
    }

    public async Task<IReadOnlyList<CoreConfigTemplateView>> GetFullConfigTemplatesAsync() =>
        (await AppManager.Instance.FullConfigTemplateItem() ?? [])
            .Select(item => new CoreConfigTemplateView(item.Id, item.Remarks, item.Enabled, item.CoreType, item.Config, item.TunConfig, item.AddProxyOnly, item.ProxyDetour))
            .ToArray();

    public async Task<OperationView> SaveFullConfigTemplateAsync(ECoreType coreType, CoreConfigTemplateInput input)
    {
        var result = await _mutations.RunAsync(async () =>
        {
            var current = await AppManager.Instance.GetFullConfigTemplateItem(coreType);
            if (current is null)
            {
                return -2;
            }
            var item = new FullConfigTemplateItem
            {
                Id = current.Id,
                Remarks = input.Remarks ?? current.Remarks,
                Enabled = input.Enabled,
                CoreType = coreType,
                Config = input.Config,
                TunConfig = input.TunConfig,
                AddProxyOnly = input.AddProxyOnly,
                ProxyDetour = input.ProxyDetour,
            };
            return await ConfigHandler.SaveFullConfigTemplate(Config, item);
        });
        if (result == -2)
        {
            return OperationView.Fail("core_template_not_found", ApiMessageKeys.CommonNotFound, new { coreType = coreType.ToString() });
        }
        if (result != 0)
        {
            return OperationView.Fail("core_template_save_failed", ApiMessageKeys.CommonInvalidInput);
        }
        _events.Publish("settings-changed", new { section = "core-template", coreType = coreType.ToString(), restartRequired = _coreStartedAt is not null });
        return OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved,
            new { coreType = coreType.ToString(), restartRequired = _coreStartedAt is not null });
    }

    public async Task<OperationView> ApplyRegionalPresetAsync(EPresetType preset)
    {
        if (preset is not (EPresetType.Default or EPresetType.Russia or EPresetType.Iran))
        {
            return OperationView.Fail("regional_preset_invalid", ApiMessageKeys.RegionalPresetInvalid);
        }
        var success = await ConfigHandler.ApplyRegionalPreset(Config, preset, mutation => _mutations.RunAsync(mutation));
        if (success)
        {
            var routingResult = await ConfigHandler.InitRouting(Config, false, mutation => _mutations.RunAsync(mutation));
            if (routingResult != 0)
            {
                return OperationView.Fail("regional_preset_failed", ApiMessageKeys.RegionalPresetFailed);
            }
            await _mutations.RunAsync(() => EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config)));
        }
        _events.Publish("settings-changed", new { section = "regional-preset", preset = preset.ToString(), restartRequired = success && _coreStartedAt is not null });
        return success
            ? OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonCompleted,
                new { preset = preset.ToString(), restartRequired = _coreStartedAt is not null })
            : OperationView.Fail("regional_preset_failed", ApiMessageKeys.RegionalPresetFailed);
    }

    public async Task<OperationView> ClearStatisticsAsync()
    {
        await _mutations.RunAsync(() => StatisticsManager.Instance.ClearAllServerStatistics());
        _latestTraffic = null;
        _events.Publish("traffic", null!);
        _events.Publish("profiles-changed", new { subscriptionId = Config.SubIndexId });
        return OperationView.Ok(ApiMessageKeys.CommonDeleted);
    }

    public Task<IReadOnlyList<string>> GetRunningOperationsAsync()
    {
        var operations = GetRunningSubscriptionUpdates().ToList();
        if (_speedtestTask is { IsCompleted: false })
        {
            operations.Add("speedtest");
        }
        if (_xrayUpdateTask is { IsCompleted: false })
        {
            operations.Add("xray-update");
        }
        if (_geoUpdateTask is { IsCompleted: false })
        {
            operations.Add("geo-update");
        }
        if (_coreStartedAt is not null)
        {
            operations.Add("core");
        }
        return Task.FromResult<IReadOnlyList<string>>(operations);
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
