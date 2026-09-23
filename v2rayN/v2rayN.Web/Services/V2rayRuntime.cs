using System.Net.Sockets;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Builder;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using v2rayN.Web.Adapters;
using v2rayN.Web.Contracts;

namespace v2rayN.Web.Services;

public sealed class V2rayRuntime(EventHub events, LogBuffer logs, IConfiguration configuration)
{
    private readonly SemaphoreSlim _coreGate = new(1, 1);
    private readonly object _subscriptionGate = new();
    private readonly Dictionary<string, Task> _subscriptionTasks = new(StringComparer.Ordinal);
    private SpeedtestService? _speedtestService;
    private DateTimeOffset? _coreStartedAt;
    private string? _xrayPath;
    private bool _initialized;

    private Config Config => AppManager.Instance.Config;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!AppManager.Instance.InitApp())
        {
            throw new InvalidOperationException("ServiceLib could not load its configuration.");
        }

        AppManager.Instance.WindowDialog = new HeadlessWindowDialog();
        if (!AppManager.Instance.InitComponents())
        {
            throw new InvalidOperationException("ServiceLib component initialization failed.");
        }

        EnsureInboundDefaults(Config);
        Config.TunModeItem.EnableTun = false;
        Config.Inbound[0].AllowLANConn = configuration.GetValue("V2RAYN_WEB_PROXY_LISTEN_ALL", false);
        await ConfigHandler.SaveConfig(Config);

        await ConfigHandler.InitBuiltinDNS(Config);
        await ConfigHandler.InitBuiltinFullConfigTemplate(Config);
        await ProfileExManager.Instance.Init();
        await CertPemManager.Instance.Init(Config);
        await CoreManager.Instance.Init(Config, OnCoreMessageAsync);
        _xrayPath = FindXrayExecutable(out var missingXrayMessage);
        if (_xrayPath is null)
        {
            AddLog("core", missingXrayMessage);
        }

        _initialized = true;
        AddLog("web", "ServiceLib initialized in headless mode.");

        if (configuration.GetValue("V2RAYN_WEB_AUTOSTART", false))
        {
            var result = await StartCoreAsync(null, cancellationToken);
            if (!result.Success)
            {
                AddLog("core", $"Automatic start skipped or failed: {result.Message}");
            }
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            await CoreManager.Instance.CoreStop();
            await ProfileExManager.Instance.SaveTo();
            await ConfigHandler.SaveConfig(Config);
            await SQLiteHelper.Instance.DisposeDbConnectionAsync();
            AddLog("web", "ServiceLib stopped.");
        }
        catch (Exception ex)
        {
            AddLog("web", $"Shutdown error: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<SubscriptionView>> GetSubscriptionsAsync()
    {
        var items = await AppManager.Instance.SubItems() ?? [];
        return items.Select(item => new SubscriptionView(
            item.Id,
            item.Remarks,
            item.Url,
            item.Enabled,
            item.AutoUpdateInterval,
            item.UpdateTime,
            item.Memo)).ToArray();
    }

    public async Task<(bool Success, string Message, SubscriptionView? Subscription)> AddSubscriptionAsync(SubscriptionInput input)
    {
        if (!TryValidateSubscription(input, out var message))
        {
            return (false, message, null);
        }

        var item = ToSubItem(input);
        item.Id = string.Empty;
        var result = await ConfigHandler.AddSubItem(Config, item);
        if (result != 0)
        {
            return (false, "ServiceLib could not save the subscription.", null);
        }

        var saved = (await AppManager.Instance.SubItems())?.FirstOrDefault(candidate => candidate.Url == item.Url);
        return saved is null
            ? (false, "Subscription was not found after saving.", null)
            : (true, "Subscription added.", ToSubscriptionView(saved));
    }

    public async Task<(bool Success, string Message, SubscriptionView? Subscription)> UpdateSubscriptionAsync(string id, SubscriptionInput input)
    {
        var existing = await AppManager.Instance.GetSubItem(id);
        if (existing is null)
        {
            return (false, "Subscription not found.", null);
        }
        if (!TryValidateSubscription(input, out var message))
        {
            return (false, message, null);
        }

        var item = ToSubItem(input);
        item.Id = id;
        item.Sort = existing.Sort;
        item.UpdateTime = existing.UpdateTime;
        item.PrevProfile = existing.PrevProfile;
        item.NextProfile = existing.NextProfile;
        item.PreSocksPort = existing.PreSocksPort;
        item.CustomCoreType = existing.CustomCoreType;
        var result = await ConfigHandler.AddSubItem(Config, item);
        return result == 0
            ? (true, "Subscription updated.", ToSubscriptionView(item))
            : (false, "ServiceLib could not update the subscription.", null);
    }

    public async Task<OperationView> DeleteSubscriptionAsync(string id)
    {
        var subscription = await AppManager.Instance.GetSubItem(id);
        if (subscription is null)
        {
            return new(false, "Subscription not found.");
        }

        var selectedProfile = await AppManager.Instance.GetProfileItem(Config.IndexId);
        var removesCurrentProfile = selectedProfile?.Subid == id;
        if (removesCurrentProfile)
        {
            await StopCoreAsync(CancellationToken.None);
        }

        await ConfigHandler.DeleteSubItem(Config, id);
        if (removesCurrentProfile)
        {
            _ = await ConfigHandler.GetDefaultServer(Config);
            await ConfigHandler.SaveConfig(Config);
        }

        AddLog("subscription", $"Deleted subscription {subscription.Remarks} ({id}).");
        return new(true, "Subscription deleted.");
    }

    public bool StartSubscriptionUpdate(string id, bool useProxy)
    {
        lock (_subscriptionGate)
        {
            if (_subscriptionTasks.TryGetValue(id, out var running) && !running.IsCompleted)
            {
                return false;
            }

            var task = Task.Run(async () =>
            {
                try
                {
                    await SubscriptionHandler.UpdateProcess(Config, id, useProxy, (success, message) =>
                    {
                        var payload = new { subscriptionId = id, success, message };
                        events.Publish("subscription-progress", payload);
                        AddLog("subscription", message);
                        return Task.CompletedTask;
                    });
                }
                catch (Exception ex)
                {
                    AddLog("subscription", $"Subscription update failed: {ex.Message}");
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
            return true;
        }
    }

    public async Task<IReadOnlyList<ProfileView>> GetProfilesAsync(string? subscriptionId, string? filter)
    {
        var profiles = await AppManager.Instance.ProfileItems(subscriptionId ?? string.Empty) ?? [];
        var subscriptions = (await AppManager.Instance.SubItems() ?? []).ToDictionary(item => item.Id, item => item.Remarks);
        var extensions = await ProfileExManager.Instance.GetProfileExs();
        var extensionMap = extensions.ToDictionary(item => item.IndexId);
        var query = filter?.Trim();

        return profiles
            .Where(item => string.IsNullOrEmpty(query)
                || item.Remarks.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Address.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(item =>
            {
                extensionMap.TryGetValue(item.IndexId, out var extension);
                return new ProfileView(
                    item.IndexId,
                    item.Remarks,
                    item.ConfigType.ToString(),
                    item.Address,
                    item.Port,
                    item.Network,
                    item.StreamSecurity,
                    item.Subid,
                    subscriptions.GetValueOrDefault(item.Subid),
                    AppManager.Instance.GetCoreType(item, item.ConfigType).ToString(),
                    extension?.Delay ?? 0,
                    extension?.Speed ?? 0,
                    extension?.IpInfo,
                    item.IndexId == Config.IndexId,
                    !item.IsComplex() && item.Port > 0);
            })
            .OrderBy(item => extensionMap.GetValueOrDefault(item.IndexId)?.Sort ?? int.MaxValue)
            .ToArray();
    }

    public async Task<StatusView> GetStatusAsync()
    {
        var selectedProfile = await AppManager.Instance.GetProfileItem(Config.IndexId);
        var profileItems = await AppManager.Instance.ProfileItems(string.Empty) ?? [];
        var subscriptions = await AppManager.Instance.SubItems() ?? [];
        var firstInbound = Config.Inbound.FirstOrDefault();
        var port = firstInbound?.LocalPort ?? 0;
        var listening = port is > 0 and <= 65535 && await IsListeningAsync(port, CancellationToken.None);
        return new StatusView(
            _coreStartedAt is not null && listening,
            _coreStartedAt is not null && listening ? AppManager.Instance.RunningCoreType.ToString() : null,
            selectedProfile?.IndexId,
            selectedProfile?.Remarks,
            listening ? _coreStartedAt : null,
            firstInbound is null
                ? []
                : [new ListenerView("mixed", ["http", "socks"], firstInbound.AllowLANConn ? "0.0.0.0" : "127.0.0.1", port, listening)],
            !string.IsNullOrEmpty(_xrayPath),
            Utils.GetRuntimeInfo(),
            profileItems.Count,
            subscriptions.Count,
            DateTimeOffset.UtcNow);
    }

    public async Task<OperationView> SelectProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        var profile = await AppManager.Instance.GetProfileItem(profileId);
        if (profile is null)
        {
            return new(false, "Profile not found.");
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
                return new(false, "Profile not found.");
            }
        }

        return await StartCoreAsync(profile, cancellationToken);
    }

    private async Task<OperationView> StartCoreAsync(ProfileItem? requestedProfile, CancellationToken cancellationToken, bool selectProfile = false)
    {
        await _coreGate.WaitAsync(cancellationToken);
        try
        {
            var profile = requestedProfile ?? await ConfigHandler.GetDefaultServer(Config);
            if (profile is null)
            {
                return new(false, "No current profile is configured.");
            }

            if (_xrayPath is null)
            {
                return new(false, "Xray-core executable was not found in the Web runtime data path.");
            }

            var built = await CoreConfigContextBuilder.BuildAll(Config, profile);
            if (!built.Success)
            {
                return new(false, string.Join(" ", built.CombinedValidatorResult.Errors));
            }
            if (built.MainResult.Context.RunCoreType != ECoreType.Xray
                || (built.PreSocksResult?.Context.RunCoreType is { } preCore && preCore != ECoreType.Xray))
            {
                return new(false, "This profile requires a core other than Xray, which is not included in this Web build.");
            }

            if (selectProfile && await ConfigHandler.SetDefaultServerIndex(Config, profile.IndexId) != 0)
            {
                return new(false, "Could not select the requested profile.");
            }

            var port = Config.Inbound.FirstOrDefault()?.LocalPort ?? 0;
            if (_coreStartedAt is null && await IsListeningAsync(port, cancellationToken))
            {
                return new(false, $"Configured proxy port {port} is already in use by another process.");
            }

            await CoreManager.Instance.LoadCore(built.MainResult.Context, built.PreSocksResult?.Context);
            var started = await WaitForListenerAsync(port, cancellationToken);
            _coreStartedAt = started ? DateTimeOffset.UtcNow : null;
            var operation = new OperationView(started, started ? $"Xray started with {profile.Remarks}." : "Xray did not open the configured mixed listener.");
            AddLog("core", operation.Message);
            events.Publish("status", await GetStatusAsync());
            return operation;
        }
        finally
        {
            _coreGate.Release();
        }
    }

    public async Task<OperationView> StopCoreAsync(CancellationToken cancellationToken)
    {
        await _coreGate.WaitAsync(cancellationToken);
        try
        {
            await CoreManager.Instance.CoreStop();
            _coreStartedAt = null;
            AddLog("core", "Core stopped.");
            events.Publish("status", await GetStatusAsync());
            return new(true, "Core stopped.");
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
            await CoreManager.Instance.CoreStop();
            _coreStartedAt = null;
            var profile = await ConfigHandler.GetDefaultServer(Config);
            if (profile is null)
            {
                return new(false, "No current profile is configured.");
            }

            if (_xrayPath is null)
            {
                return new(false, "Xray-core executable was not found in the Web runtime data path.");
            }

            var built = await CoreConfigContextBuilder.BuildAll(Config, profile);
            if (!built.Success || built.MainResult.Context.RunCoreType != ECoreType.Xray)
            {
                return new(false, string.Join(" ", built.CombinedValidatorResult.Errors));
            }

            var port = Config.Inbound.FirstOrDefault()?.LocalPort ?? 0;
            if (_coreStartedAt is null && await IsListeningAsync(port, cancellationToken))
            {
                return new(false, $"Configured proxy port {port} is already in use by another process.");
            }

            await CoreManager.Instance.LoadCore(built.MainResult.Context, built.PreSocksResult?.Context);
            var started = await WaitForListenerAsync(port, cancellationToken);
            _coreStartedAt = started ? DateTimeOffset.UtcNow : null;
            var result = new OperationView(started, started ? "Xray restarted." : "Xray did not open the configured mixed listener.");
            AddLog("core", result.Message);
            events.Publish("status", await GetStatusAsync());
            return result;
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
            return new(false, "This profile cannot be TCP latency tested.");
        }

        _speedtestService ??= new SpeedtestService(Config, result =>
        {
            events.Publish("latency-result", result);
            if (!string.IsNullOrEmpty(result.IndexId))
            {
                AddLog("latency", $"{result.IndexId}: {result.Delay}");
            }
            return Task.CompletedTask;
        });

        _ = Task.Run(() => _speedtestService.RunLoop(ESpeedActionType.Tcping, [profile]));
        events.Publish("latency-started", new { profileId });
        return new(true, "TCP latency test started.");
    }

    public OperationView StopLatencyTests()
    {
        _speedtestService?.ExitLoop();
        return new(true, "Latency test cancellation requested.");
    }

    public IReadOnlyList<LogView> GetRecentLogs(int limit) => logs.Recent(limit);

    private async Task OnCoreMessageAsync(bool notify, string message)
    {
        AddLog("core", message);
        await Task.CompletedTask;
    }

    private void AddLog(string source, string message)
    {
        var entry = logs.Add(source, message);
        events.Publish("log", entry);
    }

    private static bool TryValidateSubscription(SubscriptionInput input, out string message)
    {
        if (string.IsNullOrWhiteSpace(input.Remarks))
        {
            message = "Subscription name is required.";
            return false;
        }
        if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            message = "Subscription URL must use HTTP or HTTPS.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static SubItem ToSubItem(SubscriptionInput input) => new()
    {
        Id = string.Empty,
        Remarks = input.Remarks.Trim(),
        Url = input.Url.Trim(),
        MoreUrl = input.MoreUrl?.Trim() ?? string.Empty,
        Enabled = input.Enabled,
        UserAgent = input.UserAgent?.Trim() ?? string.Empty,
        RequestHeaders = input.RequestHeaders,
        Filter = input.Filter,
        AutoUpdateInterval = Math.Max(input.AutoUpdateInterval, 0),
        ConvertTarget = input.ConvertTarget,
        Memo = input.Memo,
    };

    private static SubscriptionView ToSubscriptionView(SubItem item) => new(
        item.Id,
        item.Remarks,
        item.Url,
        item.Enabled,
        item.AutoUpdateInterval,
        item.UpdateTime,
        item.Memo);

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
