using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class IpInfoColumnParityTests
{
    [Test]
    public async Task IpInfoColumnRequiresConfiguredApiAndDesktopColumnVisibility()
    {
        await V2rayRuntime.ShouldShowIpInfoColumn("https://ip.example.test", hideColumnIpInfo: false).Should().BeTrue();
        await V2rayRuntime.ShouldShowIpInfoColumn(" ", hideColumnIpInfo: false).Should().BeTrue();
        await V2rayRuntime.ShouldShowIpInfoColumn(string.Empty, hideColumnIpInfo: false).Should().BeFalse();
        await V2rayRuntime.ShouldShowIpInfoColumn("https://ip.example.test", hideColumnIpInfo: true).Should().BeFalse();
        await V2rayRuntime.ShouldShowIpInfoColumn(null, hideColumnIpInfo: true).Should().BeFalse();
    }
}
