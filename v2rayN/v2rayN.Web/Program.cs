using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServiceLib;
using v2rayN.Web.Contracts;
using v2rayN.Web.Services;

// ServiceLib resolves its config, SQLite, Core and log paths through LocalApplicationData.
// Set this before constructing any ServiceLib singleton so a read-only app directory is never used.
Environment.SetEnvironmentVariable(Global.LocalAppData, "1");

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<LogBuffer>();
builder.Services.AddSingleton<V2rayRuntime>();
builder.Services.AddHostedService<V2rayHostedService>(services =>
    new V2rayHostedService(services.GetRequiredService<V2rayRuntime>()));

var apiKey = Environment.GetEnvironmentVariable("V2RAYN_WEB_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    throw new InvalidOperationException("Set V2RAYN_WEB_API_KEY to a strong secret before starting the Web backend.");
}

var app = builder.Build();

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api") && path != "/api/health")
    {
        var suppliedKey = context.Request.Headers.Authorization.ToString();
        if (suppliedKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            suppliedKey = suppliedKey[7..].Trim();
        }
        else
        {
            suppliedKey = context.Request.Query["access_token"].ToString();
        }

        var expectedBytes = Encoding.UTF8.GetBytes(apiKey);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedKey);
        if (expectedBytes.Length != suppliedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "A valid bearer token is required." });
            return;
        }
    }

    await next();
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/status", async (V2rayRuntime runtime) => Results.Ok(await runtime.GetStatusAsync()));
app.MapGet("/api/logs", (int? limit, V2rayRuntime runtime) => Results.Ok(runtime.GetRecentLogs(limit ?? 200)));

app.MapGet("/api/subscriptions", async (V2rayRuntime runtime) => Results.Ok(await runtime.GetSubscriptionsAsync()));
app.MapPost("/api/subscriptions", async (SubscriptionInput input, V2rayRuntime runtime) =>
{
    var result = await runtime.AddSubscriptionAsync(input);
    return result.Success ? Results.Created($"/api/subscriptions/{result.Subscription!.Id}", result.Subscription) : Results.BadRequest(new { error = result.Message });
});
app.MapPut("/api/subscriptions/{id}", async (string id, SubscriptionInput input, V2rayRuntime runtime) =>
{
    var result = await runtime.UpdateSubscriptionAsync(id, input);
    return result.Success ? Results.Ok(result.Subscription) : Results.BadRequest(new { error = result.Message });
});
app.MapDelete("/api/subscriptions/{id}", async (string id, V2rayRuntime runtime) =>
{
    var result = await runtime.DeleteSubscriptionAsync(id);
    return result.Success ? Results.Ok(result) : Results.NotFound(new { error = result.Message });
});
app.MapPost("/api/subscriptions/{id}/update", (string id, bool? useProxy, V2rayRuntime runtime) =>
    runtime.StartSubscriptionUpdate(id, useProxy ?? false)
        ? Results.Accepted($"/api/subscriptions/{id}", new OperationView(true, "Subscription update started."))
        : Results.Conflict(new OperationView(false, "An update for this subscription is already running.")));

app.MapGet("/api/profiles", async (string? subscriptionId, string? filter, V2rayRuntime runtime) =>
    Results.Ok(await runtime.GetProfilesAsync(subscriptionId, filter)));
app.MapPost("/api/profiles/{id}/select", async (string id, V2rayRuntime runtime, CancellationToken cancellationToken) =>
{
    var result = await runtime.SelectProfileAsync(id, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.Conflict(result);
});
app.MapPost("/api/profiles/{id}/latency", async (string id, V2rayRuntime runtime) =>
{
    var result = await runtime.StartLatencyTestAsync(id);
    return result.Success ? Results.Accepted($"/api/profiles/{id}", result) : Results.BadRequest(result);
});
app.MapPost("/api/latency/stop", (V2rayRuntime runtime) => Results.Ok(runtime.StopLatencyTests()));

app.MapPost("/api/core/start", async (V2rayRuntime runtime, CancellationToken cancellationToken) =>
{
    var result = await runtime.StartCoreAsync(null, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.Conflict(result);
});
app.MapPost("/api/core/stop", async (V2rayRuntime runtime, CancellationToken cancellationToken) =>
    Results.Ok(await runtime.StopCoreAsync(cancellationToken)));
app.MapPost("/api/core/restart", async (V2rayRuntime runtime, CancellationToken cancellationToken) =>
{
    var result = await runtime.RestartCoreAsync(cancellationToken);
    return result.Success ? Results.Ok(result) : Results.Conflict(result);
});

app.MapGet("/api/events", async (HttpContext context, EventHub events) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers["Cache-Control"] = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    await foreach (var message in events.Subscribe(context.RequestAborted))
    {
        var payload = JsonSerializer.Serialize(message.Data, message.Data.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await context.Response.WriteAsync($"event: {message.Type}\ndata: {payload}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

await app.RunAsync();
