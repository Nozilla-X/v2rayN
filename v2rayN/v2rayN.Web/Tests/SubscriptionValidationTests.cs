using v2rayN.Web.Contracts;
using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class SubscriptionValidationTests
{
    [Test]
    public async Task AliasOnlySubscriptionIsValid()
    {
        var valid = V2rayRuntime.TryValidateSubscription(new SubscriptionInput("Manual group", string.Empty), out var code, out _);

        await valid.Should().BeTrue();
        await (code == "ok").Should().BeTrue();
    }

    [Test]
    public async Task NonEmptySubscriptionUrlStillRequiresAbsoluteHttpOrHttps()
    {
        var valid = V2rayRuntime.TryValidateSubscription(new SubscriptionInput("Remote group", "not-a-url"), out var code, out _);

        await valid.Should().BeFalse();
        await (code == "subscription_url_invalid").Should().BeTrue();
    }
}
