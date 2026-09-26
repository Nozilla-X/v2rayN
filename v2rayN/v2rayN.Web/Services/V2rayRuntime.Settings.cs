using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using v2rayN.Web.Contracts;

namespace v2rayN.Web.Services;

public sealed partial class V2rayRuntime
{
    private static readonly EConfigType[] WebEditableCoreTypes =
    [
        EConfigType.VMess,
        EConfigType.Custom,
        EConfigType.Shadowsocks,
        EConfigType.SOCKS,
        EConfigType.VLESS,
        EConfigType.Trojan,
        EConfigType.Hysteria2,
        EConfigType.WireGuard,
    ];

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
            Config.CoreTypeItem
                .Where(item => WebEditableCoreTypes.Contains(item.ConfigType))
                .Select(item => new CoreTypeMapping(item.ConfigType, item.CoreType))
                .ToArray(),
            new WebSettingsOptionsView(
                Global.Fingerprints.ToArray(),
                Global.UserAgent.ToArray(),
                Global.SingboxMuxs.ToArray(),
                ["reject", "skip"],
                Global.FragmentPacketsOptions.ToArray(),
                Global.destOverrideProtocols.ToArray(),
                Global.DomainStrategies.ToArray(),
                Global.DomainStrategies4Sbox.ToArray(),
                Global.DomainStrategies.AppendEmpty().ToArray(),
                Global.DomainStrategies4Sbox.ToArray())));
    }

    public async Task<OperationView> ApplySettingsAsync(SettingsApplyInput input)
    {
        var inbound = input.Inbound;
        var core = input.Core;
        var application = input.Application;
        var speed = input.SpeedTest;
        var highestPortOffset = inbound.SecondLocalPortEnabled || (inbound.AllowLANConn && inbound.NewPort4LAN) ? 2 : 0;
        if (inbound.LocalPort <= 0 || inbound.LocalPort > 65535 - highestPortOffset
            || !AreDestOverrideProtocolsValid(inbound.DestOverride))
        {
            return OperationView.Fail("inbound_port_invalid", ApiMessageKeys.SettingsInvalidPort);
        }
        if (!ValidateCoreSettingsInput(core))
        {
            return OperationView.Fail("core_setting_invalid", ApiMessageKeys.SettingsInvalidCoreValue);
        }
        if (application.GeoAutoUpdateInterval < 0
            || speed.SpeedTestTimeout <= 0 || speed.MixedConcurrencyCount <= 0
            || speed.SpeedTestPageSize is <= 0 || speed.SpeedTestDelayInterval is < 0
            || !IsHttpUrl(speed.SpeedTestUrl) || !IsHttpUrl(speed.SpeedPingTestUrl))
        {
            return OperationView.Fail("settings_apply_invalid", ApiMessageKeys.SettingsInvalidSpeedTest);
        }
        if (!AreWebCoreTypeMappingsValid(input.CoreTypes)
            || !Global.DomainStrategies.Contains(input.DomainStrategy ?? string.Empty)
            || !Global.DomainStrategies4Sbox.Contains(input.DomainStrategy4Singbox ?? string.Empty))
        {
            return OperationView.Fail("settings_apply_invalid", ApiMessageKeys.CommonInvalidInput);
        }

        var coreFingerprintBefore = GetCoreConfigurationFingerprint();
        var oldConfig = JsonUtils.DeepCopy(Config)
            ?? throw new InvalidOperationException("The current configuration could not be copied for an atomic settings apply.");
        var statisticsChanged = Config.GuiItem.EnableStatistics != application.EnableStatistics
            || Config.GuiItem.DisplayRealTimeSpeed != application.DisplayRealTimeSpeed;
        try
        {
            await _mutations.RunAsync(async () =>
            {
                var targetInbound = Config.Inbound[0];
                targetInbound.LocalPort = inbound.LocalPort;
                targetInbound.SecondLocalPortEnabled = inbound.SecondLocalPortEnabled;
                targetInbound.UdpEnabled = inbound.UdpEnabled;
                targetInbound.SniffingEnabled = inbound.SniffingEnabled;
                targetInbound.DestOverride = inbound.DestOverride?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
                targetInbound.RouteOnly = inbound.RouteOnly;
                targetInbound.AllowLANConn = inbound.AllowLANConn;
                targetInbound.NewPort4LAN = inbound.AllowLANConn && inbound.NewPort4LAN;
                targetInbound.User = inbound.User?.Trim() ?? string.Empty;
                targetInbound.Pass = inbound.Pass?.Trim() ?? string.Empty;

                ApplyCoreSettingsToConfig(core);
                Config.GuiItem.EnableStatistics = application.EnableStatistics;
                Config.GuiItem.DisplayRealTimeSpeed = application.DisplayRealTimeSpeed;
                Config.GuiItem.KeepOlderDedupl = application.KeepOlderDedupl;
                Config.GuiItem.AutoUpdateInterval = Math.Max(0, application.GeoAutoUpdateInterval);
                Config.GuiItem.RootCertProvider = application.RootCertProvider;
                Config.ConstItem.GeoSourceUrl = application.GeoSourceUrl;
                Config.ConstItem.SrsSourceUrl = application.SrsSourceUrl;
                Config.ConstItem.RouteRulesTemplateSourceUrl = application.RouteRulesTemplateSourceUrl;
                Config.ConstItem.SubConvertUrl = application.SubConvertUrl;

                Config.SpeedTestItem.SpeedTestTimeout = speed.SpeedTestTimeout;
                Config.SpeedTestItem.SpeedTestUrl = speed.SpeedTestUrl;
                Config.SpeedTestItem.SpeedPingTestUrl = speed.SpeedPingTestUrl;
                Config.SpeedTestItem.MixedConcurrencyCount = Math.Max(speed.MixedConcurrencyCount, Global.SpeedTestConcurrencyCountMin);
                Config.SpeedTestItem.IPAPIUrl = speed.IPAPIUrl;
                Config.SpeedTestItem.UdpTestTarget = speed.UdpTestTarget;
                Config.SpeedTestItem.SpeedTestPageSize = speed.SpeedTestPageSize;
                Config.SpeedTestItem.SpeedTestDelayInterval = speed.SpeedTestDelayInterval;

                EnsureCoreTypeMappings();
                foreach (var mapping in input.CoreTypes)
                {
                    var existing = Config.CoreTypeItem.FirstOrDefault(item => item.ConfigType == mapping.ConfigType);
                    if (existing is not null)
                    {
                        existing.CoreType = mapping.CoreType;
                    }
                }
                Config.RoutingBasicItem.DomainStrategy = input.DomainStrategy!;
                Config.RoutingBasicItem.DomainStrategy4Singbox = input.DomainStrategy4Singbox!;
                await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
            });
        }
        catch (Exception exception)
        {
            RestoreConfigValues(Config, oldConfig);
            try
            {
                await _mutations.RunAsync(() => EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config)));
            }
            catch (Exception rollbackException)
            {
                AddLog("settings", $"Settings rollback persistence failed: {rollbackException}");
            }
            AddLog("settings", $"Settings transaction failed before Core apply: {exception}");
            return OperationView.Fail("settings_transaction_failed", ApiMessageKeys.CommonInternal,
                new { stage = "config_save", exceptionType = exception.GetType().Name, detail = exception.Message });
        }

        if (statisticsChanged)
        {
            StatisticsManager.Instance.Close();
            _latestTraffic = null;
            if (Config.GuiItem.EnableStatistics || Config.GuiItem.DisplayRealTimeSpeed)
            {
                try
                {
                    await StatisticsManager.Instance.Init(Config, OnStatisticsUpdateAsync);
                }
                catch (Exception exception)
                {
                    AddLog("settings", $"Statistics reinitialization failed after settings were saved: {exception}");
                }
            }
        }

        var coreChanged = coreFingerprintBefore != GetCoreConfigurationFingerprint();
        return await CompleteCoreAffectingChangeAsync("all-settings", coreChanged);
    }

    public async Task<OperationView> UpdateInboundSettingsAsync(InboundSettingsInput input)
    {
        var highestPortOffset = input.SecondLocalPortEnabled || (input.AllowLANConn && input.NewPort4LAN) ? 2 : 0;
        if (input.LocalPort <= 0 || input.LocalPort > 65535 - highestPortOffset)
        {
            return OperationView.Fail("inbound_port_invalid", ApiMessageKeys.SettingsInvalidPort);
        }
        if (!AreDestOverrideProtocolsValid(input.DestOverride))
        {
            return OperationView.Fail("inbound_dest_override_invalid", ApiMessageKeys.CommonInvalidInput);
        }

        var fingerprint = GetCoreConfigurationFingerprint();
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

        return await CompleteCoreAffectingChangeAsync("inbound", fingerprint != GetCoreConfigurationFingerprint());
    }

    public async Task<OperationView> UpdateCoreSettingsAsync(CoreSettingsInput input)
    {
        if (!AreCoreSettingsOptionsValid(input))
        {
            return OperationView.Fail("core_setting_option_invalid", ApiMessageKeys.SettingsInvalidCoreValue);
        }
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

        var fingerprint = GetCoreConfigurationFingerprint();
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

        return await CompleteCoreAffectingChangeAsync("core", fingerprint != GetCoreConfigurationFingerprint());
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
        var requestedMappings = mappings.ToArray();
        if (!AreWebCoreTypeMappingsValid(requestedMappings))
        {
            return OperationView.Fail("core_type_mapping_invalid", ApiMessageKeys.CommonInvalidInput);
        }

        var fingerprint = GetCoreConfigurationFingerprint();
        await _mutations.RunAsync(async () =>
        {
            EnsureCoreTypeMappings();
            foreach (var mapping in requestedMappings)
            {
                var item = Config.CoreTypeItem.FirstOrDefault(entry => entry.ConfigType == mapping.ConfigType);
                if (item is not null)
                {
                    item.CoreType = mapping.CoreType;
                }
            }
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        });
        return await CompleteCoreAffectingChangeAsync("core-types", fingerprint != GetCoreConfigurationFingerprint());
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

        var fingerprint = GetCoreConfigurationFingerprint();
        await _mutations.RunAsync(async () =>
        {
            Config.RoutingBasicItem.DomainStrategy = input.DomainStrategy!;
            Config.RoutingBasicItem.DomainStrategy4Singbox = input.DomainStrategy4Singbox!;
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        });
        return await CompleteCoreAffectingChangeAsync("routing", fingerprint != GetCoreConfigurationFingerprint());
    }

    public Task<SimpleDNSItem> GetSimpleDNSAsync() => Task.FromResult(Config.SimpleDNSItem);

    public async Task<OperationView> UpdateSimpleDNSAsync(SimpleDNSItem input)
    {
        var fingerprint = GetCoreConfigurationFingerprint();
        await _mutations.RunAsync(async () =>
        {
            Config.SimpleDNSItem = input;
            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
        });
        return await CompleteCoreAffectingChangeAsync("dns", fingerprint != GetCoreConfigurationFingerprint());
    }

    public async Task<IReadOnlyList<DnsProfileView>> GetDnsProfilesAsync() =>
        (await AppManager.Instance.DNSItems() ?? [])
            .Select(item => new DnsProfileView(item.Id, item.Remarks, item.Enabled, item.CoreType, item.UseSystemHosts,
                item.NormalDNS, item.DomainStrategy4Freedom, item.DomainDNSAddress))
            .ToArray();

    public async Task<OperationView> UpdateDnsProfileAsync(ECoreType coreType, DnsProfileInput input)
    {
        var result = await _mutations.RunAsync(async () =>
        {
            var current = await AppManager.Instance.GetDNSItem(coreType);
            if (current is null)
            {
                return (Result: -2, Changed: false);
            }
            var changed = current.Enabled != input.Enabled
                || current.UseSystemHosts != input.UseSystemHosts
                || current.NormalDNS != input.NormalDNS
                || current.DomainStrategy4Freedom != input.DomainStrategy4Freedom
                || current.DomainDNSAddress != input.DomainDNSAddress
                || current.Remarks != (input.Remarks ?? current.Remarks);
            var item = new DNSItem
            {
                Id = current.Id,
                Remarks = input.Remarks ?? current.Remarks,
                Enabled = input.Enabled,
                CoreType = coreType,
                UseSystemHosts = input.UseSystemHosts,
                NormalDNS = input.NormalDNS,
                TunDNS = current.TunDNS,
                DomainStrategy4Freedom = input.DomainStrategy4Freedom,
                DomainDNSAddress = input.DomainDNSAddress,
            };
            return (Result: await ConfigHandler.SaveDNSItems(Config, item), Changed: changed);
        });
        if (result.Result == -2)
        {
            return OperationView.Fail("dns_profile_not_found", ApiMessageKeys.DnsProfileNotFound, new { coreType = coreType.ToString() });
        }
        if (result.Result != 0)
        {
            return OperationView.Fail("dns_profile_save_failed", ApiMessageKeys.DnsSaveFailed);
        }
        return await CompleteCoreAffectingChangeAsync("dns", result.Changed, data: new { coreType = coreType.ToString() });
    }

    public async Task<IReadOnlyList<RoutingItem>> GetRoutingProfilesAsync() => await AppManager.Instance.RoutingItems() ?? [];

    public async Task<OperationView> SaveRoutingProfileAsync(RoutingItem input, string? routingId = null)
    {
        if (string.IsNullOrWhiteSpace(input.Remarks))
        {
            return OperationView.Fail("routing_name_required", ApiMessageKeys.RoutingNameRequired);
        }
        if (!AreRoutingProfileStrategiesValid(input.DomainStrategy, input.DomainStrategy4Singbox))
        {
            return OperationView.Fail("routing_strategy_invalid", ApiMessageKeys.SettingsInvalidRoutingStrategy);
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
            var changed = string.IsNullOrEmpty(routingId);
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
                    return (Result: -2, Item: (RoutingItem?)null, Changed: false);
                }
                item = existingItem;
                changed = item.Remarks != input.Remarks
                    || item.Url != input.Url
                    || item.Enabled != input.Enabled
                    || item.Locked != input.Locked
                    || item.CustomIcon != input.CustomIcon
                    || item.CustomRulesetPath4Singbox != input.CustomRulesetPath4Singbox
                    || item.DomainStrategy != input.DomainStrategy
                    || item.DomainStrategy4Singbox != input.DomainStrategy4Singbox
                    || item.RuleSet != ruleJson;
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
            return (Result: result, Item: (RoutingItem?)item, Changed: changed);
        });
        if (save.Result == -2)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        if (save.Result != 0 || save.Item is null)
        {
            return OperationView.Fail("routing_profile_save_failed", ApiMessageKeys.RoutingSaveFailed);
        }

        return await CompleteCoreAffectingChangeAsync("routing-profiles", save.Changed && save.Item.IsActive,
            data: new { routingId = save.Item.Id });
    }

    public async Task<OperationView> DeleteRoutingProfileAsync(string id)
    {
        var removed = await _mutations.RunAsync(async () =>
        {
            var item = await AppManager.Instance.GetRoutingItem(id);
            if (item is null)
            {
                return (Removed: false, AffectsCore: false);
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
            return (Removed: true, AffectsCore: item.IsActive);
        });
        if (!removed.Removed)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        return await CompleteCoreAffectingChangeAsync("routing-profiles", removed.AffectsCore,
            ApiMessageKeys.CommonDeleted, new { routingId = id });
    }

    public async Task<OperationView> ActivateRoutingProfileAsync(string id)
    {
        var result = await _mutations.RunAsync(async () =>
        {
            var item = await AppManager.Instance.GetRoutingItem(id);
            if (item is null)
            {
                return (Result: -2, Changed: false);
            }
            var changed = !item.IsActive;
            return (Result: changed ? await ConfigHandler.SetDefaultRouting(Config, item) : 0, Changed: changed);
        });
        if (result.Result == -2)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        if (result.Result != 0)
        {
            return OperationView.Fail("routing_profile_activate_failed", ApiMessageKeys.RoutingSaveFailed);
        }
        return await CompleteCoreAffectingChangeAsync("routing-profiles", result.Changed,
            ApiMessageKeys.CommonCompleted, new { routingId = id });
    }

    public async Task<IReadOnlyList<RulesItem>> GetRoutingRulesAsync(string routingId)
    {
        var item = await AppManager.Instance.GetRoutingItem(routingId);
        return JsonUtils.Deserialize<List<RulesItem>>(item?.RuleSet ?? "[]") ?? [];
    }

    public async Task<OperationView> SaveRoutingRulesAsync(string routingId, IEnumerable<RulesItem> rules)
    {
        var items = rules.ToArray();
        var before = await AppManager.Instance.GetRoutingItem(routingId);
        var changed = before is not null && before.IsActive
            && before.RuleSet != JsonUtils.Serialize(items, false);
        var saved = await _mutations.RunAsync(() => SaveRoutingRulesLockedAsync(routingId, items));
        if (saved == -2)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        if (saved != 0)
        {
            return OperationView.Fail("routing_rules_save_failed", ApiMessageKeys.RoutingSaveFailed);
        }
        return await CompleteCoreAffectingChangeAsync("routing-rules", changed, data: new { routingId });
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

    private async Task<string> GetActiveRoutingSignatureAsync()
    {
        var active = (await AppManager.Instance.RoutingItems() ?? []).FirstOrDefault(item => item.IsActive);
        return active is null
            ? string.Empty
            : System.Text.Json.JsonSerializer.Serialize(new
            {
                active.Id,
                active.RuleSet,
                active.Enabled,
                active.DomainStrategy,
                active.DomainStrategy4Singbox,
                active.CustomRulesetPath4Singbox,
            });
    }

    public async Task<OperationView> ImportRoutingRulesAsync(string routingId, RouteRulesImportInput request)
    {
        var incoming = JsonUtils.Deserialize<List<RulesItem>>(request.Content);
        if (incoming is null)
        {
            return OperationView.Fail("routing_rules_json_invalid", ApiMessageKeys.RoutingRulesInvalid);
        }
        var activeBefore = await AppManager.Instance.GetRoutingItem(routingId);
        var beforeRuleSet = activeBefore?.RuleSet;
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
        if (saved == -2)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        if (saved != 0)
        {
            return OperationView.Fail("routing_rules_save_failed", ApiMessageKeys.RoutingSaveFailed);
        }
        var activeAfter = await AppManager.Instance.GetRoutingItem(routingId);
        var changed = activeBefore?.IsActive == true && activeAfter?.RuleSet != beforeRuleSet;
        return await CompleteCoreAffectingChangeAsync("routing-rules", changed, data: new { routingId });
    }

    public async Task<OperationView> ImportRoutingRulesFromUrlAsync(string routingId, bool append, CancellationToken cancellationToken)
    {
        var item = await AppManager.Instance.GetRoutingItem(routingId);
        if (item is null)
        {
            return OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound);
        }
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return OperationView.Fail("routing_url_invalid", "routing.urlInvalid");
        }

        var download = new DownloadService();
        var content = await download.TryDownloadString(uri.AbsoluteUri, true, string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return OperationView.Fail("routing_url_download_failed", "routing.urlImportFailedGeneric");
        }

        return await ImportRoutingRulesAsync(routingId, new RouteRulesImportInput(content, append));
    }

    public async Task<OperationView> MoveRoutingRuleAsync(string routingId, string ruleId, EMove direction, int position)
    {
        var routeWasActive = (await AppManager.Instance.GetRoutingItem(routingId))?.IsActive == true;
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
        if (result != 0)
        {
            return result switch
            {
                -2 => OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound),
                -3 => OperationView.Fail("routing_rule_not_found", ApiMessageKeys.RoutingRuleNotFound),
                -4 => OperationView.Fail("routing_rule_move_failed", ApiMessageKeys.CommonInvalidInput),
                _ => OperationView.Fail("routing_rules_save_failed", ApiMessageKeys.RoutingSaveFailed),
            };
        }
        return await CompleteCoreAffectingChangeAsync("routing-rules", routeWasActive, data: new { routingId });
    }

    public async Task<OperationView> DeleteRoutingRuleAsync(string routingId, string ruleId)
    {
        var routeWasActive = (await AppManager.Instance.GetRoutingItem(routingId))?.IsActive == true;
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
        if (result != 0)
        {
            return result switch
            {
                -2 => OperationView.Fail("routing_profile_not_found", ApiMessageKeys.RoutingProfileNotFound),
                -3 => OperationView.Fail("routing_rule_not_found", ApiMessageKeys.RoutingRuleNotFound),
                _ => OperationView.Fail("routing_rules_save_failed", ApiMessageKeys.RoutingSaveFailed),
            };
        }
        return await CompleteCoreAffectingChangeAsync("routing-rules", routeWasActive, data: new { routingId });
    }

    public async Task<OperationView> ImportRoutingProfilesAsync()
    {
        var before = await GetActiveRoutingSignatureAsync();
        var result = await _mutations.RunAsync(() => ConfigHandler.InitRouting(Config, true));
        if (result != 0)
        {
            return OperationView.Fail("routing_import_failed", ApiMessageKeys.RoutingSaveFailed);
        }
        return await CompleteCoreAffectingChangeAsync("routing-profiles", before != await GetActiveRoutingSignatureAsync(),
            ApiMessageKeys.CommonCompleted);
    }

    public async Task<IReadOnlyList<CoreConfigTemplateView>> GetFullConfigTemplatesAsync() =>
        (await AppManager.Instance.FullConfigTemplateItem() ?? [])
            .Select(item => new CoreConfigTemplateView(item.Id, item.Remarks, item.Enabled, item.CoreType, item.Config, item.AddProxyOnly, item.ProxyDetour))
            .ToArray();

    public async Task<OperationView> SaveFullConfigTemplateAsync(ECoreType coreType, CoreConfigTemplateInput input)
    {
        var existingBefore = await AppManager.Instance.GetFullConfigTemplateItem(coreType);
        var changed = existingBefore is not null
            && (existingBefore.Remarks != (input.Remarks ?? existingBefore.Remarks)
                || existingBefore.Enabled != input.Enabled
                || existingBefore.Config != input.Config
                || existingBefore.AddProxyOnly != input.AddProxyOnly
                || existingBefore.ProxyDetour != input.ProxyDetour);
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
                TunConfig = current.TunConfig,
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
        return await CompleteCoreAffectingChangeAsync("core-template",
            changed && CurrentCoreRuntime.CoreType == coreType,
            data: new { coreType = coreType.ToString() });
    }

    public async Task<OperationView> ApplyRegionalPresetAsync(EPresetType preset)
    {
        if (preset is not (EPresetType.Default or EPresetType.Russia or EPresetType.Iran))
        {
            return OperationView.Fail("regional_preset_invalid", ApiMessageKeys.RegionalPresetInvalid);
        }
        var success = await _mutations.RunAsync(async () =>
        {
            if (!await ConfigHandler.ApplyRegionalPreset(Config, preset))
            {
                return false;
            }

            if (await ConfigHandler.InitRouting(Config, false) != 0)
            {
                return false;
            }

            await EnsureConfigSaveSucceededAsync(() => ConfigHandler.SaveConfig(Config));
            return true;
        });
        if (success)
        {
            return await CompleteCoreAffectingChangeAsync("regional-preset", true,
                ApiMessageKeys.CommonCompleted, new { preset = preset.ToString() });
        }
        return OperationView.Fail("regional_preset_failed", ApiMessageKeys.RegionalPresetFailed);
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
        operations.AddRange(GetRunningCoreUpdateOperations());
        if (CurrentCoreRuntime.State != CoreRuntimeState.Stopped)
        {
            operations.Add("core");
        }
        return Task.FromResult<IReadOnlyList<string>>(operations);
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    internal static bool IsWebEditableCoreType(EConfigType configType) => WebEditableCoreTypes.Contains(configType);

    internal static bool AreWebCoreTypeMappingsValid(IReadOnlyCollection<CoreTypeMapping> mappings) =>
        mappings.All(mapping => IsWebEditableCoreType(mapping.ConfigType) && Enum.IsDefined(mapping.CoreType))
        && mappings.Select(mapping => mapping.ConfigType).Distinct().Count() == mappings.Count;

    internal static bool AreDestOverrideProtocolsValid(IEnumerable<string>? protocols) =>
        protocols is null || protocols.All(Global.destOverrideProtocols.Contains);

    internal static bool AreCoreSettingsOptionsValid(CoreSettingsInput input) =>
        Global.Fingerprints.Contains(input.DefFingerprint ?? string.Empty)
        && (string.IsNullOrEmpty(input.DefUserAgent) || Global.UserAgent.Contains(input.DefUserAgent))
        && Global.SingboxMuxs.Contains(input.Mux4SboxProtocol ?? string.Empty)
        && (string.IsNullOrEmpty(input.Mux4RayXudpProxyUDP443)
            || input.Mux4RayXudpProxyUDP443 is "reject" or "skip")
        && (string.IsNullOrEmpty(input.FragmentPackets)
            || Global.FragmentPacketsOptions.Contains(input.FragmentPackets));

    internal static bool AreRoutingProfileStrategiesValid(string? domainStrategy, string? domainStrategy4Singbox) =>
        Global.DomainStrategies.AppendEmpty().Contains(domainStrategy ?? string.Empty)
        && Global.DomainStrategies4Sbox.Contains(domainStrategy4Singbox ?? string.Empty);

    private string GetCoreConfigurationFingerprint() => System.Text.Json.JsonSerializer.Serialize(new
    {
        Inbound = Config.Inbound.Select(item => new
        {
            item.LocalPort,
            Protocol = item.Protocol ?? string.Empty,
            item.UdpEnabled,
            item.SniffingEnabled,
            DestOverride = item.DestOverride ?? [],
            item.RouteOnly,
            item.AllowLANConn,
            item.NewPort4LAN,
            User = item.User ?? string.Empty,
            Pass = item.Pass ?? string.Empty,
            item.SecondLocalPortEnabled,
        }),
        CoreBasic = new
        {
            Config.CoreBasicItem.LogEnabled,
            Loglevel = Config.CoreBasicItem.Loglevel ?? string.Empty,
            DefFingerprint = Config.CoreBasicItem.DefFingerprint ?? string.Empty,
            DefUserAgent = Config.CoreBasicItem.DefUserAgent ?? string.Empty,
            SendThrough = Config.CoreBasicItem.SendThrough ?? string.Empty,
            BindInterface = Config.CoreBasicItem.BindInterface ?? string.Empty,
            Config.CoreBasicItem.EnableFragment,
            Config.CoreBasicItem.EnableFinalFragment,
            Config.CoreBasicItem.EnableCacheFile4Sbox,
        },
        Mux4Ray = new
        {
            Config.Mux4RayItem.Concurrency,
            Config.Mux4RayItem.XudpConcurrency,
            XudpProxyUDP443 = Config.Mux4RayItem.XudpProxyUDP443 ?? string.Empty,
        },
        Mux4Sbox = new
        {
            Protocol = Config.Mux4SboxItem.Protocol ?? string.Empty,
            Config.Mux4SboxItem.MaxConnections,
            Config.Mux4SboxItem.Padding,
        },
        Config.HysteriaItem.UpMbps,
        Config.HysteriaItem.DownMbps,
        Fragment = new
        {
            Packets = Config.Fragment4RayItem?.Packets ?? string.Empty,
            Lengths = Config.Fragment4RayItem?.Lengths ?? [],
            Delays = Config.Fragment4RayItem?.Delays ?? [],
            MaxSplit = Config.Fragment4RayItem?.MaxSplit ?? string.Empty,
        },
        Config.SimpleDNSItem,
        Routing = new
        {
            DomainStrategy = Config.RoutingBasicItem.DomainStrategy ?? string.Empty,
            DomainStrategy4Singbox = Config.RoutingBasicItem.DomainStrategy4Singbox ?? string.Empty,
        },
        CoreTypeMapping = Config.CoreTypeItem ?? [],
    });

    private async Task<OperationView> CompleteCoreAffectingChangeAsync(
        string section,
        bool changed,
        string successMessageKey = ApiMessageKeys.CommonSaved,
        object? data = null)
    {
        var restarted = false;
        var wasRunning = changed && CurrentCoreRuntime.State == CoreRuntimeState.Running;
        if (wasRunning)
        {
            var restart = await RestartCoreAsync(CancellationToken.None);
            if (!restart.Success)
            {
                var runtime = CurrentCoreRuntime;
                var failureData = ToResultDictionary(data);
                failureData["section"] = section;
                failureData["configSaved"] = true;
                failureData["restartRequired"] = true;
                failureData["runtimeState"] = runtime.State.ToString().ToLowerInvariant();
                failureData["configuredProxyPort"] = Config.Inbound.FirstOrDefault()?.LocalPort;
                failureData["runningProxyPort"] = runtime.ProxyPort;
                failureData["coreResultCode"] = restart.Code;
                failureData["detail"] = runtime.LastFailure;
                _events.Publish("settings-changed", new { section, restartRequired = true, restartFailed = true });
                AddLog("settings", $"Settings for {section} were saved but Core apply failed: {restart.Code}; {runtime.LastFailure}");
                return OperationView.Fail("settings_core_apply_failed", ApiMessageKeys.SettingsCoreApplyFailed, failureData);
            }
            restarted = true;
        }

        _events.Publish("settings-changed", new { section, restartRequired = false, coreRestarted = restarted });
        var resultData = ToResultDictionary(data);
        resultData["changed"] = changed;
        resultData["coreRestarted"] = restarted;
        resultData["restartRequired"] = false;
        return OperationView.Ok(restarted ? ApiMessageKeys.CoreRestarted : successMessageKey, resultData);
    }

    private static Dictionary<string, object?> ToResultDictionary(object? data)
    {
        if (data is IReadOnlyDictionary<string, object?> readOnly)
        {
            return new Dictionary<string, object?>(readOnly, StringComparer.Ordinal);
        }
        if (data is IDictionary<string, object?> mutable)
        {
            return new Dictionary<string, object?>(mutable, StringComparer.Ordinal);
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (data is not null)
        {
            foreach (var property in data.GetType().GetProperties())
            {
                result[property.Name] = property.GetValue(data);
            }
        }
        return result;
    }

    private static bool ValidateCoreSettingsInput(CoreSettingsInput input)
    {
        var fragmentLengths = input.FragmentLengths ?? [];
        var fragmentDelays = input.FragmentDelays ?? [];
        return AreCoreSettingsOptionsValid(input)
            && Global.LogLevels.Contains(input.Loglevel ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            && input.Mux4RayConcurrency is >= 0 and <= 1024
            && input.Mux4RayXudpConcurrency is >= 0 and <= 1024
            && input.Mux4SboxMaxConnections is >= 0 and <= 1024
            && input.Hy2UpMbps >= 0
            && input.Hy2DownMbps >= 0
            && fragmentLengths.All(value => Utils.TryParseRange(value, 0, int.MaxValue, out _, out _))
            && fragmentDelays.All(value => Utils.TryParseRange(value, 0, int.MaxValue, out _, out _))
            && (string.IsNullOrWhiteSpace(input.FragmentMaxSplit)
                || Utils.TryParseMaxSplit(input.FragmentMaxSplit, 0, 10000, out _, out _));
    }

    private void ApplyCoreSettingsToConfig(CoreSettingsInput input)
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
        Config.Fragment4RayItem.Lengths = input.FragmentLengths?.ToList() ?? [];
        Config.Fragment4RayItem.Delays = input.FragmentDelays?.ToList() ?? [];
        Config.Fragment4RayItem.MaxSplit = input.FragmentMaxSplit;
    }

    private static void RestoreConfigValues(Config target, Config snapshot)
    {
        foreach (var property in typeof(Config).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (property.CanRead && property.CanWrite)
            {
                property.SetValue(target, property.GetValue(snapshot));
            }
        }
    }
}
