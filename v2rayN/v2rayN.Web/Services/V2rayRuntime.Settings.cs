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
        EnsureCoreTypeMappings();
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
        await ConfigHandler.SaveConfig(Config);

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

        var current = Config.TunModeItem;
        var oldTun = new TunModeItem
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
        var wasRunning = _coreStartedAt is not null;
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
        await ConfigHandler.SaveConfig(Config);

        if (wasRunning)
        {
            var restart = await RestartCoreAsync(CancellationToken.None);
            if (!restart.Success)
            {
                Config.TunModeItem = oldTun;
                await ConfigHandler.SaveConfig(Config);
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
            var status = File.ReadAllLines("/proc/self/status").FirstOrDefault(line => line.StartsWith("CapEff:", StringComparison.Ordinal));
            var capabilityHex = status?.Split(':', 2).ElementAtOrDefault(1)?.Trim();
            if (ulong.TryParse(capabilityHex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var capabilities)
                && (capabilities & (1UL << 12)) != 0)
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
        await ConfigHandler.SaveConfig(Config);

        var restartRequired = _coreStartedAt is not null;
        _events.Publish("settings-changed", new { section = "core", restartRequired });
        return OperationView.Ok(restartRequired ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved, new { restartRequired });
    }

    public async Task<OperationView> UpdateAppSettingsAsync(AppSettingsInput input)
    {
        var statisticsChanged = Config.GuiItem.EnableStatistics != input.EnableStatistics
            || Config.GuiItem.DisplayRealTimeSpeed != input.DisplayRealTimeSpeed;

        Config.GuiItem.EnableStatistics = input.EnableStatistics;
        Config.GuiItem.DisplayRealTimeSpeed = input.DisplayRealTimeSpeed;
        Config.GuiItem.KeepOlderDedupl = input.KeepOlderDedupl;
        Config.GuiItem.AutoUpdateInterval = Math.Max(0, input.GeoAutoUpdateInterval);
        Config.GuiItem.RootCertProvider = input.RootCertProvider;
        Config.ConstItem.GeoSourceUrl = input.GeoSourceUrl;
        Config.ConstItem.SrsSourceUrl = input.SrsSourceUrl;
        Config.ConstItem.RouteRulesTemplateSourceUrl = input.RouteRulesTemplateSourceUrl;
        Config.ConstItem.SubConvertUrl = input.SubConvertUrl;
        await ConfigHandler.SaveConfig(Config);

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

        Config.SpeedTestItem.SpeedTestTimeout = input.SpeedTestTimeout;
        Config.SpeedTestItem.SpeedTestUrl = input.SpeedTestUrl;
        Config.SpeedTestItem.SpeedPingTestUrl = input.SpeedPingTestUrl;
        Config.SpeedTestItem.MixedConcurrencyCount = Math.Max(input.MixedConcurrencyCount, Global.SpeedTestConcurrencyCountMin);
        Config.SpeedTestItem.IPAPIUrl = input.IPAPIUrl;
        Config.SpeedTestItem.UdpTestTarget = input.UdpTestTarget;
        Config.SpeedTestItem.SpeedTestPageSize = input.SpeedTestPageSize;
        Config.SpeedTestItem.SpeedTestDelayInterval = input.SpeedTestDelayInterval;
        await ConfigHandler.SaveConfig(Config);
        _events.Publish("settings-changed", new { section = "speedtest", restartRequired = false });
        return OperationView.Ok(ApiMessageKeys.SpeedTestSettingsSaved);
    }

    public async Task<OperationView> UpdateCoreTypeMappingsAsync(IEnumerable<CoreTypeMapping> mappings)
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
        await ConfigHandler.SaveConfig(Config);
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

        Config.RoutingBasicItem.DomainStrategy = input.DomainStrategy!;
        Config.RoutingBasicItem.DomainStrategy4Singbox = input.DomainStrategy4Singbox!;
        await ConfigHandler.SaveConfig(Config);
        _events.Publish("settings-changed", new { section = "routing", restartRequired = _coreStartedAt is not null });
        return OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved, new { restartRequired = _coreStartedAt is not null });
    }

    public Task<SimpleDNSItem> GetSimpleDNSAsync() => Task.FromResult(Config.SimpleDNSItem);

    public async Task<OperationView> UpdateSimpleDNSAsync(SimpleDNSItem input)
    {
        Config.SimpleDNSItem = input;
        await ConfigHandler.SaveConfig(Config);
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
        var current = await AppManager.Instance.GetDNSItem(coreType);
        if (current is null)
        {
            return OperationView.Fail("dns_profile_not_found", ApiMessageKeys.DnsProfileNotFound, new { coreType = coreType.ToString() });
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
        var result = await ConfigHandler.SaveDNSItems(Config, item);
        _events.Publish("settings-changed", new { section = "dns", coreType = coreType.ToString(), restartRequired = _coreStartedAt is not null });
        return result == 0
            ? OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved,
                new { coreType = coreType.ToString(), restartRequired = _coreStartedAt is not null })
            : OperationView.Fail("dns_profile_save_failed", ApiMessageKeys.DnsSaveFailed);
    }

    public async Task<IReadOnlyList<RoutingItem>> GetRoutingProfilesAsync() => await AppManager.Instance.RoutingItems() ?? [];

    public async Task<OperationView> SaveRoutingProfileAsync(RoutingItem input, string? routingId = null)
    {
        if (string.IsNullOrWhiteSpace(input.Remarks))
        {
            return OperationView.Fail("routing_name_required", ApiMessageKeys.RoutingNameRequired);
        }

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
                return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
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

        var ruleJson = string.IsNullOrWhiteSpace(input.RuleSet) ? "[]" : input.RuleSet;
        var parsedRules = JsonUtils.Deserialize<List<RulesItem>>(ruleJson);
        if (parsedRules is null)
        {
            return OperationView.Fail("routing_rules_json_invalid", ApiMessageKeys.RoutingRulesInvalid);
        }
        var rules = parsedRules;
        if (rules.Count == 0)
        {
            item.RuleSet = "[]";
            item.RuleNum = 0;
        }
        else
        {
            item.RuleNum = rules.Count;
        }
        var saved = rules.Count == 0
            ? await ConfigHandler.SaveRoutingItem(Config, item)
            : await ConfigHandler.AddBatchRoutingRules(item, JsonUtils.Serialize(rules, false));
        if (saved != 0)
        {
            return OperationView.Fail("routing_profile_save_failed", ApiMessageKeys.RoutingSaveFailed);
        }

        _events.Publish("settings-changed", new { section = "routing-profiles", restartRequired = _coreStartedAt is not null });
        return OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved,
            new { routingId = item.Id, restartRequired = _coreStartedAt is not null });
    }

    public async Task<OperationView> DeleteRoutingProfileAsync(string id)
    {
        var item = await AppManager.Instance.GetRoutingItem(id);
        if (item is null)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        await ConfigHandler.RemoveRoutingItem(item);
        if (item.IsActive)
        {
            var remaining = await AppManager.Instance.RoutingItems() ?? [];
            var fallback = remaining.FirstOrDefault();
            if (fallback is not null)
            {
                await ConfigHandler.SetDefaultRouting(Config, fallback);
            }
            else
            {
                Config.RoutingBasicItem.RoutingIndexId = string.Empty;
                await ConfigHandler.SaveConfig(Config);
            }
        }
        _events.Publish("settings-changed", new { section = "routing-profiles", restartRequired = _coreStartedAt is not null });
        return OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonDeleted,
            new { routingId = id, restartRequired = _coreStartedAt is not null });
    }

    public async Task<OperationView> ActivateRoutingProfileAsync(string id)
    {
        var item = await AppManager.Instance.GetRoutingItem(id);
        if (item is null)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        if (!item.IsActive)
        {
            await ConfigHandler.SetDefaultRouting(Config, item);
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
        var item = await AppManager.Instance.GetRoutingItem(routingId);
        if (item is null)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        var data = JsonUtils.Serialize(rules.ToList(), false);
        var result = await ConfigHandler.AddBatchRoutingRules(item, data);
        _events.Publish("settings-changed", new { section = "routing-rules", routingId, restartRequired = _coreStartedAt is not null });
        return result == 0
            ? OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved,
                new { routingId, restartRequired = _coreStartedAt is not null })
            : OperationView.Fail("routing_rules_save_failed", ApiMessageKeys.RoutingSaveFailed);
    }

    public async Task<OperationView> ImportRoutingRulesAsync(string routingId, RouteRulesImportInput request)
    {
        var item = await AppManager.Instance.GetRoutingItem(routingId);
        if (item is null)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }

        var incoming = JsonUtils.Deserialize<List<RulesItem>>(request.Content);
        if (incoming is null)
        {
            return OperationView.Fail("routing_rules_json_invalid", ApiMessageKeys.RoutingRulesInvalid);
        }
        if (request.Append)
        {
            var existing = JsonUtils.Deserialize<List<RulesItem>>(item.RuleSet ?? "[]") ?? [];
            existing.AddRange(incoming);
            incoming = existing;
        }

        return await SaveRoutingRulesAsync(routingId, incoming);
    }

    public async Task<OperationView> MoveRoutingRuleAsync(string routingId, string ruleId, EMove direction, int position)
    {
        var item = await AppManager.Instance.GetRoutingItem(routingId);
        if (item is null)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        var rules = JsonUtils.Deserialize<List<RulesItem>>(item.RuleSet ?? "[]") ?? [];
        var index = rules.FindIndex(rule => rule.Id == ruleId);
        if (index < 0)
        {
            return OperationView.Fail("routing_rule_not_found", ApiMessageKeys.RoutingRuleNotFound);
        }
        if (await ConfigHandler.MoveRoutingRule(rules, index, direction, position) != 0)
        {
            return OperationView.Fail("routing_rule_move_failed", ApiMessageKeys.CommonInvalidInput);
        }
        return await SaveRoutingRulesAsync(routingId, rules);
    }

    public async Task<OperationView> DeleteRoutingRuleAsync(string routingId, string ruleId)
    {
        var rules = (await GetRoutingRulesAsync(routingId)).Where(rule => rule.Id != ruleId).ToList();
        if (rules.Count == (await GetRoutingRulesAsync(routingId)).Count)
        {
            return OperationView.Fail("routing_rule_not_found", ApiMessageKeys.RoutingRuleNotFound);
        }
        return await SaveRoutingRulesAsync(routingId, rules);
    }

    public async Task<OperationView> ImportRoutingProfilesAsync()
    {
        var result = await ConfigHandler.InitRouting(Config, true);
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
        var current = await AppManager.Instance.GetFullConfigTemplateItem(coreType);
        if (current is null)
        {
            return OperationView.Fail("core_template_not_found", ApiMessageKeys.CommonNotFound, new { coreType = coreType.ToString() });
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
        var result = await ConfigHandler.SaveFullConfigTemplate(Config, item);
        _events.Publish("settings-changed", new { section = "core-template", coreType = coreType.ToString(), restartRequired = _coreStartedAt is not null });
        return result == 0
            ? OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonSaved,
                new { coreType = coreType.ToString(), restartRequired = _coreStartedAt is not null })
            : OperationView.Fail("core_template_save_failed", ApiMessageKeys.CommonInvalidInput);
    }

    public async Task<OperationView> ApplyRegionalPresetAsync(EPresetType preset)
    {
        if (preset is not (EPresetType.Default or EPresetType.Russia or EPresetType.Iran))
        {
            return OperationView.Fail("regional_preset_invalid", ApiMessageKeys.RegionalPresetInvalid);
        }
        var success = await ConfigHandler.ApplyRegionalPreset(Config, preset);
        if (success)
        {
            await ConfigHandler.InitRouting(Config);
            await ConfigHandler.SaveConfig(Config);
        }
        _events.Publish("settings-changed", new { section = "regional-preset", preset = preset.ToString(), restartRequired = success && _coreStartedAt is not null });
        return success
            ? OperationView.Ok(_coreStartedAt is not null ? ApiMessageKeys.CommonCoreRestartRequired : ApiMessageKeys.CommonCompleted,
                new { preset = preset.ToString(), restartRequired = _coreStartedAt is not null })
            : OperationView.Fail("regional_preset_failed", ApiMessageKeys.RegionalPresetFailed);
    }

    public async Task<OperationView> ClearStatisticsAsync()
    {
        await StatisticsManager.Instance.ClearAllServerStatistics();
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
