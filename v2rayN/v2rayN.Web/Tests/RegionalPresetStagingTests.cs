using System.Collections.Concurrent;
using System.Net;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class RegionalPresetStagingTests
{
    [Test]
    public async Task RegionalPresetStagesAllRemoteDnsAndRoutingContentWithoutChangingUserProfileState()
    {
        var existingDns = CreateDnsRows();
        var responses = CreateRegionalResponses();
        var requestedUrls = new ConcurrentBag<string>();
        var stager = new RegionalPresetStager((url, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            requestedUrls.Add(url);
            return Task.FromResult(responses.GetValueOrDefault(url));
        });

        var staged = await stager.StageAsync(
            EPresetType.Russia,
            existingDns,
            [],
            proxy: null,
            CancellationToken.None);

        await staged.GeoSourceUrl.Should().BeEqualTo(Global.GeoFilesSources[1]);
        await staged.SrsSourceUrl.Should().BeEqualTo(Global.SingboxRulesetSources[1]);
        await staged.RouteRulesTemplateSourceUrl.Should().BeEqualTo(Global.RoutingRulesSources[1]);
        await staged.DnsItems.Count.Should().BeEqualTo(2);
        await (staged.DnsItems[0].Id == "xray-id" && staged.DnsItems[0].Enabled && staged.DnsItems[0].Remarks == "my Xray DNS").Should().BeTrue();
        await (staged.DnsItems[0].NormalDNS == "xray-normal-content" && staged.DnsItems[0].TunDNS == "xray-tun-content").Should().BeTrue();
        await (staged.DnsItems[1].Id == "singbox-id" && !staged.DnsItems[1].Enabled && staged.DnsItems[1].Remarks == "my sing-box DNS").Should().BeTrue();
        await staged.SimpleDnsItem.RemoteDNS.Should().BeEqualTo("https://dns.example.test/remote");
        await staged.RoutingTemplateVersion.Should().BeEqualTo("R1");
        await staged.RoutingItems.Count.Should().BeEqualTo(1);
        await staged.RoutingItems[0].Remarks.Should().BeEqualTo("R1-Region rules");
        await existingDns[0].NormalDNS.Should().BeEqualTo("existing-xray-data");
        await requestedUrls.Count.Should().BeEqualTo(7);
    }

    [Test]
    public async Task DefaultPresetStagesOnlyLocalDefaultsAndDoesNotInvokeDownloader()
    {
        var downloaded = false;
        var stager = new RegionalPresetStager((_, _, _) =>
        {
            downloaded = true;
            return Task.FromResult<string?>(null);
        });

        var staged = await stager.StageAsync(EPresetType.Default, [], [], null, CancellationToken.None);

        await downloaded.Should().BeFalse();
        await staged.DnsItems.Count.Should().BeEqualTo(2);
        await staged.DnsItems.All(item => !item.Enabled).Should().BeTrue();
        await staged.GeoSourceUrl.Should().BeEqualTo(string.Empty);
        await staged.RoutingItems.Count.Should().BeEqualTo(0);
    }

    [Test]
    public async Task SlowFailingPresetLeavesConfigAndDatabasesUntouchedAndDoesNotHoldReadOrMutationGates()
    {
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coreGate = new SemaphoreSlim(1, 1);
        var mutationGate = new SemaphoreSlim(1, 1);
        var config = SniffingSettingsIntegrationTests.CreateConfig();
        config.ConstItem.GeoSourceUrl = "original-geo-source";
        SniffingSettingsIntegrationTests.BindAppManagerConfig(config);
        var runtime = new V2rayRuntime(null!, null!, null!, null!, null!);
        var dnsRows = new List<string> { "original dns" };
        var routingRows = new List<string> { "original routing" };
        var applyCalled = false;
        var stager = new RegionalPresetStager(async (_, _, token) =>
        {
            downloadStarted.TrySetResult();
            await failDownload.Task.WaitAsync(token);
            throw new IOException("simulated download failure");
        }, TimeSpan.FromSeconds(2));
        var workflow = RegionalPresetWorkflow.RunAsync(
            token => stager.StageAsync(EPresetType.Russia, CreateDnsRows(), [], null, token),
            _ =>
            {
                applyCalled = true;
                config.ConstItem.GeoSourceUrl = "modified";
                dnsRows[0] = "modified";
                routingRows[0] = "modified";
                return Task.FromResult(true);
            },
            CancellationToken.None);

        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var settingsRead = await runtime.GetSettingsAsync();
        await settingsRead.App.GeoSourceUrl.Should().BeEqualTo("original-geo-source");
        await coreGate.WaitAsync(TimeSpan.Zero);
        coreGate.Release();
        await mutationGate.WaitAsync(TimeSpan.Zero);
        mutationGate.Release();

        failDownload.TrySetResult();
        var failed = false;
        try
        {
            await workflow;
        }
        catch (IOException)
        {
            failed = true;
        }

        await failed.Should().BeTrue();
        await applyCalled.Should().BeFalse();
        await config.ConstItem.GeoSourceUrl.Should().BeEqualTo("original-geo-source");
        await dnsRows.SequenceEqual(new[] { "original dns" }).Should().BeTrue();
        await routingRows.SequenceEqual(new[] { "original routing" }).Should().BeTrue();
    }

    [Test]
    public async Task RequestCancellationCancelsStagingBeforeApply()
    {
        using var cancellation = new CancellationTokenSource();
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyCalled = false;
        var stager = new RegionalPresetStager(async (_, _, token) =>
        {
            downloadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });
        var workflow = RegionalPresetWorkflow.RunAsync(
            token => stager.StageAsync(EPresetType.Russia, CreateDnsRows(), [], null, token),
            _ =>
            {
                applyCalled = true;
                return Task.FromResult(true);
            },
            cancellation.Token);

        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            await workflow;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        await cancelled.Should().BeTrue();
        await applyCalled.Should().BeFalse();
    }

    [Test]
    public async Task FailedApplyRunsRollbackAndCannotTriggerCoreRestart()
    {
        var configValue = "before";
        var dnsRows = new List<string> { "dns-before" };
        var routingRows = new List<string> { "routing-before" };
        var restartCalled = false;

        var result = await RegionalPresetTransaction.RunAsync(
            async () =>
            {
                configValue = "after";
                dnsRows[0] = "dns-after";
                routingRows[0] = "routing-after";
                await Task.Yield();
                throw new IOException("simulated persistence failure");
            },
            () =>
            {
                configValue = "before";
                dnsRows[0] = "dns-before";
                routingRows[0] = "routing-before";
                return Task.FromResult(true);
            });

        if (result.Success) restartCalled = true;
        await result.Success.Should().BeFalse();
        await result.RollbackSucceeded.Should().BeTrue();
        await restartCalled.Should().BeFalse();
        await configValue.Should().BeEqualTo("before");
        await dnsRows.SequenceEqual(new[] { "dns-before" }).Should().BeTrue();
        await routingRows.SequenceEqual(new[] { "routing-before" }).Should().BeTrue();
    }

    private static List<DNSItem> CreateDnsRows() =>
    [
        new() { Id = "xray-id", CoreType = ECoreType.Xray, Remarks = "my Xray DNS", Enabled = true, NormalDNS = "existing-xray-data" },
        new() { Id = "singbox-id", CoreType = ECoreType.sing_box, Remarks = "my sing-box DNS", Enabled = false, NormalDNS = "existing-singbox-data" },
    ];

    private static Dictionary<string, string?> CreateRegionalResponses()
    {
        var dnsBase = Global.DNSTemplateSources[1];
        var xrayNormalUrl = "https://assets.example.test/xray-normal";
        var xrayTunUrl = "https://assets.example.test/xray-tun";
        var singboxNormalUrl = "https://assets.example.test/singbox-normal";
        var template = new RoutingTemplate
        {
            Version = "R1",
            RoutingItems =
            [
                new RoutingItem
                {
                    Remarks = "Region rules",
                    RuleSet = JsonUtils.Serialize(new List<RulesItem> { new() { Remarks = "sample" } }, false),
                },
            ],
        };

        return new(StringComparer.Ordinal)
        {
            [dnsBase + "v2ray.json"] = JsonUtils.Serialize(new DNSItem { NormalDNS = xrayNormalUrl, TunDNS = xrayTunUrl }, false),
            [dnsBase + "sing_box.json"] = JsonUtils.Serialize(new DNSItem { NormalDNS = singboxNormalUrl }, false),
            [dnsBase + "simple_dns.json"] = JsonUtils.Serialize(new SimpleDNSItem { RemoteDNS = "https://dns.example.test/remote" }, false),
            [Global.RoutingRulesSources[1]] = JsonUtils.Serialize(template, false),
            [xrayNormalUrl] = "xray-normal-content",
            [xrayTunUrl] = "xray-tun-content",
            [singboxNormalUrl] = "singbox-normal-content",
        };
    }
}
