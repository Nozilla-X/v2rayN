namespace v2rayN.Web.Services;

public sealed class V2rayHostedService(V2rayRuntime runtime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => runtime.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => runtime.ShutdownAsync(cancellationToken);
}
