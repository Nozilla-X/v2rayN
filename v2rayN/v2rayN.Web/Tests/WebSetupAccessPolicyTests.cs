using System.Net;
using Microsoft.AspNetCore.Http;
using v2rayN.Web.Security;

namespace v2rayN.Web.Tests;

public class WebSetupAccessPolicyTests
{
    [Test]
    public async Task Ipv4LoopbackAndAllowedHostsCanInitialize()
    {
        await WebSetupAccessPolicy.IsAllowed(IPAddress.Loopback, "localhost", false).Should().BeTrue();
        await WebSetupAccessPolicy.IsAllowed(IPAddress.Loopback, "127.0.0.1", false).Should().BeTrue();
    }

    [Test]
    public async Task Ipv6LoopbackAndBracketedHostCanInitialize()
    {
        await WebSetupAccessPolicy.IsAllowed(IPAddress.IPv6Loopback, "::1", false).Should().BeTrue();
        await WebSetupAccessPolicy.IsLocalHost("[::1]").Should().BeTrue();
        await WebSetupAccessPolicy.IsLocalHost(new HostString("[::1]:5080").Host).Should().BeTrue();
    }

    [Test]
    public async Task RemoteAddressAndRebindingHostAreDenied()
    {
        await WebSetupAccessPolicy.IsAllowed(IPAddress.Parse("192.0.2.25"), "localhost", false).Should().BeFalse();
        await WebSetupAccessPolicy.IsAllowed(IPAddress.Loopback, "attacker.example", false).Should().BeFalse();
        await WebSetupAccessPolicy.IsAllowed(IPAddress.Loopback, "127.0.0.2", false).Should().BeFalse();
    }

    [Test]
    public async Task PrivateNetworkClientCanInitializeUsingTheServerPrivateIp()
    {
        var serverAddress = IPAddress.Parse("192.168.86.221");
        await WebSetupAccessPolicy.IsAllowed(
            IPAddress.Parse("192.168.86.149"), "192.168.86.221", false, serverAddress).Should().BeTrue();
        await WebSetupAccessPolicy.IsAllowed(
            IPAddress.Parse("10.147.17.168"), "192.168.86.221", false, serverAddress).Should().BeTrue();
        await WebSetupAccessPolicy.IsAllowed(
            IPAddress.Parse("192.168.86.149"), "192.168.86.222", false, serverAddress).Should().BeFalse();
        await WebSetupAccessPolicy.IsAllowed(
            IPAddress.Parse("8.8.8.8"), "192.168.86.221", false, serverAddress).Should().BeFalse();
    }

    [Test]
    public async Task ForwardingHeadersNeverGrantSetupAccess()
    {
        await WebSetupAccessPolicy.IsAllowed(IPAddress.Loopback, "localhost", true).Should().BeFalse();
        var headers = new HeaderDictionary { ["X-Forwarded-Host"] = "localhost" };
        await WebSetupAccessPolicy.HasForwardedHeaders(headers).Should().BeTrue();
        await WebSetupAccessPolicy.IsAllowed(
            IPAddress.Loopback,
            "localhost",
            WebSetupAccessPolicy.HasForwardedHeaders(headers)).Should().BeFalse();
    }
}
