using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServiceLib;
using v2rayN.Web.Api;
using v2rayN.Web.Contracts;
using v2rayN.Web.Services;

// An optional XDG data-home override lets systemd and container services share the same
// ServiceLib path behavior. Without it, ServiceLib keeps its native writable-app-dir/fallback policy.
var dataHome = Environment.GetEnvironmentVariable("V2RAYN_DATA_HOME");
if (!string.IsNullOrWhiteSpace(dataHome))
{
    var fullDataHome = Path.GetFullPath(dataHome);
    Directory.CreateDirectory(fullDataHome);
    Environment.SetEnvironmentVariable("XDG_DATA_HOME", fullDataHome);
    Environment.SetEnvironmentVariable(Global.LocalAppData, "1");
}

var applicationBase = AppContext.BaseDirectory;
var webRoot = Path.Combine(applicationBase, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = applicationBase,
    WebRootPath = Directory.Exists(webRoot) ? webRoot : null,
});
if (string.IsNullOrWhiteSpace(builder.Configuration[Microsoft.AspNetCore.Hosting.WebHostDefaults.ServerUrlsKey])
    && string.IsNullOrWhiteSpace(builder.Configuration["http_ports"]))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
}
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<LogBuffer>();
builder.Services.AddSingleton<RuntimeOperationCoordinator>();
builder.Services.AddSingleton<V2rayRuntime>();
builder.Services.AddHostedService<V2rayHostedService>(services =>
    new V2rayHostedService(services.GetRequiredService<V2rayRuntime>()));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

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
        else if (path == "/api/events")
        {
            suppliedKey = context.Request.Query["access_token"].ToString();
        }

        var expectedBytes = Encoding.UTF8.GetBytes(apiKey);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedKey);
        if (expectedBytes.Length != suppliedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail("unauthorized", ApiMessageKeys.CommonUnauthorized));
            return;
        }
    }

    await next();
});

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (BadHttpRequestException exception) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
    {
        app.Logger.LogInformation(exception, "Invalid API request.");
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail("request_invalid", ApiMessageKeys.CommonInvalidInput));
    }
    catch (System.Text.Json.JsonException exception) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
    {
        app.Logger.LogInformation(exception, "Invalid API JSON payload.");
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail("json_invalid", ApiMessageKeys.CommonInvalidInput));
    }
    catch (OperationCanceledException) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    }
    catch (Exception exception) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
    {
        app.Logger.LogError(exception, "Unhandled API request failure.");
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail("internal_error", ApiMessageKeys.CommonInternal));
    }
});

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api")
        || context.Request.Path == "/api/health"
        || context.Request.Path == "/api/events")
    {
        await next();
        return;
    }

    var path = context.Request.Path.Value?.TrimEnd('/') ?? string.Empty;
    var ownedByBackgroundOperation =
        (HttpMethods.IsPost(context.Request.Method)
            && (path is "/api/core/xray/update" or "/api/core/geo/update" or "/api/speedtests"
                or "/api/subscriptions/update" or "/api/backup/restore" or "/api/backup/webdav/restore"))
        || (HttpMethods.IsPost(context.Request.Method)
            && path.StartsWith("/api/subscriptions/", StringComparison.Ordinal)
            && path.EndsWith("/update", StringComparison.Ordinal));

    if (ownedByBackgroundOperation)
    {
        await next();
        return;
    }

    var operations = context.RequestServices.GetRequiredService<RuntimeOperationCoordinator>();
    var exclusive = (HttpMethods.IsGet(context.Request.Method) && path == "/api/backup/download")
        || (HttpMethods.IsPost(context.Request.Method) && path == "/api/backup/webdav");
    await using var operation = exclusive
        ? await operations.EnterExclusiveAsync(context.RequestAborted)
        : await operations.EnterOperationAsync(context.RequestAborted);
    context.RequestAborted = operation.Token;
    await next();
});

app.MapWebApi();
app.MapFallback("/api/{**path}", () =>
    Results.NotFound(ApiEnvelope<object>.Fail("route_not_found", ApiMessageKeys.CommonRouteNotFound)));

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

await app.RunAsync();
