using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class RuntimeRequestOperationPolicyTests
{
    [Test]
    [Arguments("GET", "/api/status")]
    [Arguments("GET", "/api/operations")]
    [Arguments("GET", "/api/logs")]
    [Arguments("GET", "/api/logs/page")]
    public async Task ReadOnlyRuntimeEndpointsUseObservationLeases(string method, string path)
    {
        await RuntimeRequestOperationPolicy.Classify(method, path)
            .Should().BeEqualTo(RuntimeRequestOperationKind.Observation);
    }

    [Test]
    [Arguments("POST", "/api/core/xray/update")]
    [Arguments("POST", "/api/core-updates/Xray/update")]
    [Arguments("POST", "/api/core-updates/sing_box/update")]
    [Arguments("POST", "/api/core/geo/update")]
    [Arguments("POST", "/api/speedtests")]
    [Arguments("POST", "/api/subscriptions/example/update")]
    [Arguments("POST", "/api/backup/restore")]
    [Arguments("POST", "/api/backup/webdav/restore")]
    public async Task LongRunningRequestStartsItsOwnBackgroundLease(string method, string path)
    {
        await RuntimeRequestOperationPolicy.Classify(method, path)
            .Should().BeEqualTo(RuntimeRequestOperationKind.Background);
    }

    [Test]
    [Arguments("GET", "/api/backup/download")]
    [Arguments("POST", "/api/backup/webdav")]
    public async Task BackupSnapshotRequestsUseExclusiveLeases(string method, string path)
    {
        await RuntimeRequestOperationPolicy.Classify(method, path)
            .Should().BeEqualTo(RuntimeRequestOperationKind.Exclusive);
    }

    [Test]
    public async Task OtherApiRequestsUseSharedLeases()
    {
        await RuntimeRequestOperationPolicy.Classify("POST", "/api/core/start")
            .Should().BeEqualTo(RuntimeRequestOperationKind.Shared);
    }
}
