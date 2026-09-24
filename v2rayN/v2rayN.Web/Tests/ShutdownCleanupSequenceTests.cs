using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class ShutdownCleanupSequenceTests
{
    [Test]
    public async Task ActiveOperationDrainTimeoutSkipsDatabaseClose()
    {
        var coordinator = new RuntimeOperationCoordinator();
        await using var activeOperation = await coordinator.EnterOperationAsync();
        var databaseCloseStarted = false;
        var messages = new List<string>();
        var steps = new ShutdownCleanupStep[]
        {
            new("operation drain", async deadline =>
                await coordinator.StopAndDrainAsync(deadline.Token, deadline.Remaining)),
            new("database close", _ =>
            {
                databaseCloseStarted = true;
                return Task.FromResult(true);
            }),
        };

        var completed = await ShutdownCleanupSequence.RunAsync(
            steps,
            TimeSpan.FromMilliseconds(80),
            CancellationToken.None,
            messages.Add);

        await completed.Should().BeFalse();
        await databaseCloseStarted.Should().BeFalse();
        await messages.Any(message => message.Contains("operation drain", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue();
        await activeOperation.DisposeAsync();
    }

    [Test]
    public async Task TimedOutCleanupStepPreventsLaterSharedResourceCleanup()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletingStep = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var databaseCloseStarted = false;
        var steps = new ShutdownCleanupStep[]
        {
            new("profile save", _ =>
            {
                started.TrySetResult(true);
                return neverCompletingStep.Task;
            }),
            new("database close", _ =>
            {
                databaseCloseStarted = true;
                return Task.FromResult(true);
            }),
        };

        var completed = await ShutdownCleanupSequence.RunAsync(
            steps,
            TimeSpan.FromMilliseconds(80),
            CancellationToken.None,
            _ => { });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await completed.Should().BeFalse();
        await databaseCloseStarted.Should().BeFalse();
        neverCompletingStep.TrySetResult(true);
    }

    [Test]
    public async Task ShutdownUsesOneFiniteOverallBudget()
    {
        var neverCompletingStep = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new ShutdownCleanupStep[]
        {
            new("first cleanup", async _ =>
            {
                await Task.Delay(50);
                return true;
            }),
            new("second cleanup", _ => neverCompletingStep.Task),
            new("third cleanup", _ => Task.FromResult(true)),
        };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var completed = await ShutdownCleanupSequence.RunAsync(
            steps,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None,
            _ => { });

        await completed.Should().BeFalse();
        await stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        neverCompletingStep.TrySetResult(true);
    }
}
