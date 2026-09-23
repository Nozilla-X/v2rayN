using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class RuntimeOperationCoordinatorTests
{
    [Test]
    public async Task ExclusiveLeaseBlocksNewSharedOperations()
    {
        var coordinator = new RuntimeOperationCoordinator();
        var exclusive = await coordinator.EnterExclusiveAsync();
        var pending = coordinator.EnterOperationAsync().AsTask();

        await Task.Delay(25);
        await pending.IsCompleted.Should().BeFalse();

        await exclusive.DisposeAsync();
        await using var shared = await pending;
        await shared.Token.IsCancellationRequested.Should().BeFalse();
    }

    [Test]
    public async Task ShutdownCancelsAndDrainsActiveLeases()
    {
        var coordinator = new RuntimeOperationCoordinator();
        var operation = await coordinator.EnterOperationAsync();
        var shutdown = coordinator.StopAndDrainAsync(CancellationToken.None);

        for (var attempt = 0; attempt < 100 && !operation.Token.IsCancellationRequested; attempt++)
        {
            await Task.Delay(10);
        }
        await operation.Token.IsCancellationRequested.Should().BeTrue();
        await operation.DisposeAsync();
        await shutdown;
    }
}
