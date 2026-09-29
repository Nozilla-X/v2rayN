using System.Net;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;

namespace v2rayN.Web.Services;

internal sealed record RegionalPresetStage(
    EPresetType Preset,
    string GeoSourceUrl,
    string SrsSourceUrl,
    string RouteRulesTemplateSourceUrl,
    SimpleDNSItem SimpleDnsItem,
    IReadOnlyList<DNSItem> DnsItems,
    string? RoutingTemplateVersion,
    IReadOnlyList<RoutingItem> RoutingItems);

internal sealed class RegionalPresetStager(
    Func<string, IWebProxy?, CancellationToken, Task<string?>> download,
    TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<RegionalPresetStage> StageAsync(
        EPresetType preset,
        IReadOnlyList<DNSItem> existingDnsItems,
        IReadOnlyList<RoutingItem> existingRoutingItems,
        IWebProxy? proxy,
        CancellationToken cancellationToken)
    {
        if (preset == EPresetType.Default)
        {
            return new(
                preset,
                string.Empty,
                string.Empty,
                string.Empty,
                ConfigHandler.InitBuiltinSimpleDNS(),
                [CreateDefaultDns(ECoreType.Xray, "V2ray"), CreateDefaultDns(ECoreType.sing_box, "sing-box")],
                null,
                []);
        }

        var sourceIndex = preset switch
        {
            EPresetType.Russia => 1,
            EPresetType.Iran => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unsupported regional preset."),
        };
        var xrayItem = existingDnsItems.FirstOrDefault(item => item.CoreType == ECoreType.Xray)
            ?? throw new InvalidOperationException("The Xray DNS profile is missing.");
        var singboxItem = existingDnsItems.FirstOrDefault(item => item.CoreType == ECoreType.sing_box)
            ?? throw new InvalidOperationException("The sing-box DNS profile is missing.");
        var dnsBaseUrl = Global.DNSTemplateSources[sourceIndex];
        var routeTemplateUrl = Global.RoutingRulesSources[sourceIndex];

        var xrayTask = StageDnsProfileAsync(ECoreType.Xray, xrayItem, dnsBaseUrl + "v2ray.json", proxy, cancellationToken);
        var singboxTask = StageDnsProfileAsync(ECoreType.sing_box, singboxItem, dnsBaseUrl + "sing_box.json", proxy, cancellationToken);
        var simpleDnsTask = StageSimpleDnsAsync(dnsBaseUrl + "simple_dns.json", proxy, cancellationToken);
        var routingTask = StageRoutingAsync(routeTemplateUrl, existingRoutingItems, proxy, cancellationToken);
        await Task.WhenAll(xrayTask, singboxTask, simpleDnsTask, routingTask);

        var routing = await routingTask;
        return new(
            preset,
            Global.GeoFilesSources[sourceIndex],
            Global.SingboxRulesetSources[sourceIndex],
            routeTemplateUrl,
            await simpleDnsTask,
            [await xrayTask, await singboxTask],
            routing.Version,
            routing.Items);
    }

    private async Task<DNSItem> StageDnsProfileAsync(
        ECoreType coreType,
        DNSItem existing,
        string url,
        IWebProxy? proxy,
        CancellationToken cancellationToken)
    {
        var content = await DownloadRequiredAsync(url, proxy, cancellationToken);
        var template = JsonUtils.Deserialize<DNSItem>(content)
            ?? throw new InvalidDataException($"The {coreType} DNS template is invalid.");

        var normalDnsTask = string.IsNullOrWhiteSpace(template.NormalDNS)
            ? Task.CompletedTask
            : DownloadNormalDnsAsync();
        var tunDnsTask = string.IsNullOrWhiteSpace(template.TunDNS)
            ? Task.CompletedTask
            : DownloadTunDnsAsync();
        await Task.WhenAll(normalDnsTask, tunDnsTask);

        template.Id = existing.Id;
        template.Remarks = existing.Remarks;
        template.Enabled = existing.Enabled;
        template.CoreType = coreType;
        return template;

        async Task DownloadNormalDnsAsync()
        {
            template.NormalDNS = await DownloadRequiredAsync(template.NormalDNS!, proxy, cancellationToken);
        }

        async Task DownloadTunDnsAsync()
        {
            template.TunDNS = await DownloadRequiredAsync(template.TunDNS!, proxy, cancellationToken);
        }
    }

    private async Task<SimpleDNSItem> StageSimpleDnsAsync(string url, IWebProxy? proxy, CancellationToken cancellationToken)
    {
        var content = await DownloadRequiredAsync(url, proxy, cancellationToken);
        return JsonUtils.Deserialize<SimpleDNSItem>(content)
            ?? throw new InvalidDataException("The Simple DNS template is invalid.");
    }

    private async Task<(string Version, IReadOnlyList<RoutingItem> Items)> StageRoutingAsync(
        string url,
        IReadOnlyList<RoutingItem> existingItems,
        IWebProxy? proxy,
        CancellationToken cancellationToken)
    {
        var content = await DownloadRequiredAsync(url, proxy, cancellationToken);
        var template = JsonUtils.Deserialize<RoutingTemplate>(content)
            ?? throw new InvalidDataException("The routing template is invalid.");
        if (string.IsNullOrWhiteSpace(template.Version) || template.RoutingItems is null)
        {
            throw new InvalidDataException("The routing template is incomplete.");
        }

        if (existingItems.Any(item => item.Remarks?.StartsWith(template.Version, StringComparison.Ordinal) == true))
        {
            return (template.Version, []);
        }

        var stagedTasks = template.RoutingItems
            .Select((sourceItem, index) => StageRoutingItemAsync(
                sourceItem,
                template.Version,
                existingItems.Count + index + 1,
                proxy,
                cancellationToken))
            .ToArray();
        var stagedItems = await Task.WhenAll(stagedTasks);
        return (template.Version, stagedItems.Where(item => item is not null).Cast<RoutingItem>().ToArray());
    }

    private async Task<RoutingItem?> StageRoutingItemAsync(
        RoutingItem sourceItem,
        string templateVersion,
        int sort,
        IWebProxy? proxy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sourceItem.Url) && string.IsNullOrWhiteSpace(sourceItem.RuleSet))
        {
            return null;
        }

        var rulesContent = !string.IsNullOrWhiteSpace(sourceItem.RuleSet)
            ? sourceItem.RuleSet
            : await DownloadRequiredAsync(sourceItem.Url, proxy, cancellationToken);
        var rules = JsonUtils.Deserialize<List<RulesItem>>(rulesContent)
            ?? throw new InvalidDataException($"The routing rules for '{sourceItem.Remarks}' are invalid.");
        var item = JsonUtils.DeepCopy(sourceItem)
            ?? throw new InvalidDataException("A routing template item could not be copied.");
        item.Id = string.Empty;
        item.Remarks = $"{templateVersion}-{item.Remarks}";
        item.Enabled = true;
        item.Sort = sort;
        item.Url = string.Empty;
        item.IsActive = false;
        item.RuleNum = rules.Count;
        item.RuleSet = JsonUtils.Serialize(rules, false);
        return item;
    }

    private async Task<string> DownloadRequiredAsync(string url, IWebProxy? proxy, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            var content = await download(url, proxy, timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidDataException($"The download returned no content: {url}");
            }
            return content;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The download exceeded {_timeout.TotalSeconds:0} seconds: {url}");
        }
    }

    private static DNSItem CreateDefaultDns(ECoreType coreType, string remarks) => new()
    {
        Remarks = remarks,
        CoreType = coreType,
        Enabled = false,
    };
}

internal static class RegionalPresetWorkflow
{
    public static async Task<TResult> RunAsync<TStage, TResult>(
        Func<CancellationToken, Task<TStage>> stage,
        Func<TStage, Task<TResult>> apply,
        CancellationToken cancellationToken)
    {
        var staged = await stage(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await apply(staged);
    }
}

internal sealed record RegionalPresetApplyResult(bool Success, bool RollbackSucceeded, Exception? Error);

internal static class RegionalPresetTransaction
{
    public static async Task<RegionalPresetApplyResult> RunAsync(Func<Task> apply, Func<Task<bool>> rollback)
    {
        try
        {
            await apply();
            return new(true, true, null);
        }
        catch (Exception exception)
        {
            var rollbackSucceeded = false;
            try
            {
                rollbackSucceeded = await rollback();
            }
            catch
            {
                // Rollback failure is represented in the result; preserve the original apply failure.
            }
            return new(false, rollbackSucceeded, exception);
        }
    }
}
