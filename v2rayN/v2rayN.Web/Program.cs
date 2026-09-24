using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServiceLib;
using ServiceLib.Common;
using v2rayN.Web.Api;
using v2rayN.Web.Contracts;
using v2rayN.Web.Launcher;
using v2rayN.Web.Security;
using v2rayN.Web.Services;

internal static class Program
{
    private const string ApiKeyEnvironmentVariable = "V2RAYN_WEB_API_KEY";

    public static async Task<int> Main(string[] args)
    {
        PrepareDataScope();

        var launchOptions = WebLaunchOptions.Parse(
            args,
            OperatingSystem.IsLinux(),
            LauncherEnvironment.IsDaemonEnvironment(ReadEnvironment()),
            IsContainerEnvironment());

        if (launchOptions.Mode == WebLaunchMode.BackgroundLauncher)
        {
            if (!OperatingSystem.IsLinux())
            {
                Console.Error.WriteLine(LauncherMessages.StartFailed(GetLauncherLocale()));
                return 1;
            }

            var (healthUri, webUiUri) = GetLauncherUris(launchOptions.HostArguments);
            var launcher = new WebLauncher(new HttpWebHealthProbe(), new LinuxXdgBrowserOpener(), GetLauncherLocale());
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                Console.Error.WriteLine(LauncherMessages.StartFailed(GetLauncherLocale()));
                return 1;
            }

            var lockPath = GetInstanceLockPath();
            return await launcher.RunAsync(
                processPath,
                launchOptions.HostArguments,
                lockPath,
                healthUri,
                webUiUri,
                launchOptions.NoOpen);
        }

        if (launchOptions.Mode == WebLaunchMode.BackgroundChild)
        {
            BackgroundChildProcess.DetachStandardHandles();
        }

        var instanceLockPath = GetInstanceLockPath();
        if (!WebInstanceLock.TryAcquire(instanceLockPath, writeOwner: true, out var instanceLock))
        {
            if (launchOptions.Mode == WebLaunchMode.BackgroundChild)
            {
                return 73;
            }

            Console.Error.WriteLine(LauncherMessages.InstanceInUse(GetLauncherLocale()));
            return 73;
        }

        using (instanceLock)
        {
            return await RunWebHostAsync(launchOptions.HostArguments);
        }
    }

    private static async Task<int> RunWebHostAsync(string[] args)
    {
        var configPath = Utils.GetConfigPath("web-auth.json");
        var webAuth = new WebAuthService(configPath, Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable));
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
        builder.Services.AddSingleton(webAuth);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var isApi = path.StartsWithSegments("/api");
            var isPublicApi = path == "/api/health"
                || path == "/api/setup/status"
                || path == "/api/setup";
            if (!isApi || isPublicApi)
            {
                await next();
                return;
            }

            var suppliedKey = context.Request.Headers.Authorization.ToString();
            if (suppliedKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                suppliedKey = suppliedKey[7..].Trim();
            }
            else if (path == "/api/events")
            {
                suppliedKey = context.Request.Query["access_token"].ToString();
            }

            if (webAuth.SetupRequired || !webAuth.ValidateKey(suppliedKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail("unauthorized", ApiMessageKeys.CommonUnauthorized));
                return;
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
            catch (JsonException exception) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
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
                || context.Request.Path == "/api/events"
                || context.Request.Path.StartsWithSegments("/api/setup"))
            {
                await next();
                return;
            }

            var path = context.Request.Path.Value?.TrimEnd('/') ?? string.Empty;
            var leaseKind = RuntimeRequestOperationPolicy.Classify(context.Request.Method, path);
            if (leaseKind == RuntimeRequestOperationKind.Background)
            {
                await next();
                return;
            }

            var operations = context.RequestServices.GetRequiredService<RuntimeOperationCoordinator>();
            await using var operation = leaseKind switch
            {
                RuntimeRequestOperationKind.Exclusive => await operations.EnterExclusiveAsync(context.RequestAborted),
                RuntimeRequestOperationKind.Observation => await operations.EnterObservationAsync(context.RequestAborted),
                _ => await operations.EnterOperationAsync(context.RequestAborted),
            };
            context.RequestAborted = operation.Token;
            await next();
        });

        app.MapWebApi();
        app.MapWebSetupEndpoints();
        app.MapFallback("/api/{**path}", () =>
            Results.NotFound(ApiEnvelope<object>.Fail("route_not_found", ApiMessageKeys.CommonRouteNotFound)));

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapFallbackToFile("index.html");

        await app.RunAsync();
        return 0;
    }

    private static void PrepareDataScope()
    {
        var dataHome = Environment.GetEnvironmentVariable("V2RAYN_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome))
        {
            var fullDataHome = Path.GetFullPath(dataHome);
            Directory.CreateDirectory(fullDataHome);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", fullDataHome);
            Environment.SetEnvironmentVariable(Global.LocalAppData, "1");
            return;
        }

        if (!Utils.HasWritePermission())
        {
            Environment.SetEnvironmentVariable(Global.LocalAppData, "1");
        }
    }

    private static string GetInstanceLockPath() =>
        Path.Combine(Utils.StartupPath(), "v2rayN.Web.instance.lock");

    private static (Uri HealthUri, Uri WebUiUri) GetLauncherUris(string[] hostArguments)
    {
        var configuredUrl = hostArguments
            .Select((argument, index) => (argument, index))
            .Where(item => item.argument == "--urls" && item.index + 1 < hostArguments.Length)
            .Select(item => hostArguments[item.index + 1])
            .FirstOrDefault()
            ?? hostArguments.FirstOrDefault(argument => argument.StartsWith("--urls=", StringComparison.Ordinal))?[7..]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(port => $"http://127.0.0.1:{port}").FirstOrDefault();

        var baseUri = new Uri("http://127.0.0.1:5080");
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            foreach (var item in configuredUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = item.Replace("*", "127.0.0.1", StringComparison.Ordinal)
                    .Replace("+", "127.0.0.1", StringComparison.Ordinal);
                if (Uri.TryCreate(normalized, UriKind.Absolute, out var candidate)
                    && (candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps))
                {
                    baseUri = new UriBuilder(candidate) { Host = "127.0.0.1", Path = "/" }.Uri;
                    break;
                }
            }
        }

        var healthUri = new Uri(baseUri, "api/health");
        return (healthUri, baseUri);
    }

    private static bool IsContainerEnvironment()
    {
        var environment = ReadEnvironment();
        return LauncherEnvironment.IsContainerEnvironment(environment)
            || File.Exists("/.dockerenv")
            || File.Exists("/run/.containerenv");
    }

    private static Dictionary<string, string?> ReadEnvironment() =>
        Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.OrdinalIgnoreCase);

    private static LauncherLocale GetLauncherLocale()
    {
        var environment = ReadEnvironment();
        return LauncherMessages.ResolveLocale(
            environment.GetValueOrDefault("LC_ALL"),
            environment.GetValueOrDefault("LC_MESSAGES"),
            environment.GetValueOrDefault("LANG"),
            CultureInfo.CurrentUICulture.Name);
    }
}
