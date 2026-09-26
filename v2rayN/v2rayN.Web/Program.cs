using System.Collections;
using System.Globalization;
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
    private const string ManagementKeyEnvironmentVariable = "V2RAYN_WEB_API_KEY";

    public static async Task<int> Main(string[] args)
    {
        PrepareDataScope();

        var environment = ReadEnvironment();
        var daemonEnvironment = LauncherEnvironment.IsDaemonEnvironment(environment);
        var containerEnvironment = IsContainerEnvironment();
        var launchOptions = WebLaunchOptions.Parse(
            args,
            OperatingSystem.IsLinux(),
            daemonEnvironment,
            containerEnvironment);

        if (launchOptions.Mode == WebLaunchMode.Stop)
        {
            if (!OperatingSystem.IsLinux())
            {
                Console.Error.WriteLine(LauncherMessages.StopUnsupported(GetLauncherLocale()));
                return 1;
            }

            var (stopHealthUri, _) = GetLauncherUris(launchOptions.HostArguments);
            var stopper = new WebStopper(new HttpWebHealthProbe(), new LinuxProcessSignalSender());
            var result = await stopper.StopAsync(GetInstanceLockPath(), stopHealthUri);
            Console.Out.WriteLine(LauncherMessages.StopMessage(result, GetLauncherLocale(), stopper.LastObservedShutdownStage));
            return result is WebStopResult.NotRunning or WebStopResult.Stopped ? 0 : 1;
        }

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

        WebHostRunResult hostResult;
        using (instanceLock)
        {
            var showForegroundPrompt = launchOptions.Mode == WebLaunchMode.Foreground
                && args.Contains(WebLaunchOptions.ForegroundFlag, StringComparer.Ordinal)
                && !daemonEnvironment
                && !containerEnvironment;
            hostResult = await RunWebHostAsync(launchOptions.HostArguments, showForegroundPrompt);
        }

        if (hostResult.RestartRequested && launchOptions.Mode == WebLaunchMode.BackgroundChild
            && !StartNativeRestartChild(launchOptions.HostArguments))
        {
            Console.Error.WriteLine("The Web instance stopped after restore, but the native launcher could not start its replacement process.");
            return 1;
        }
        return hostResult.ExitCode;
    }

    private static async Task<WebHostRunResult> RunWebHostAsync(string[] args, bool showForegroundPrompt)
    {
        var configPath = WebAuthStorage.MigrateAndGetPath(
            Utils.StartupPath(),
            Utils.GetConfigPath("web-auth.json"));
        var webAuth = new WebAuthService(configPath, Environment.GetEnvironmentVariable(ManagementKeyEnvironmentVariable));
        var applicationBase = AppContext.BaseDirectory;
        var webRoot = Path.Combine(applicationBase, "wwwroot");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = applicationBase,
            WebRootPath = Directory.Exists(webRoot) ? webRoot : null,
        });
        // EventSource carries only a one-time, short-lived SSE ticket in its URL; keep
        // request lifecycle logs from recording that ticket as well.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        if (string.IsNullOrWhiteSpace(builder.Configuration[Microsoft.AspNetCore.Hosting.WebHostDefaults.ServerUrlsKey])
            && string.IsNullOrWhiteSpace(builder.Configuration["http_ports"]))
        {
            builder.WebHost.UseUrls("http://0.0.0.0:5080");
        }

        builder.Services.AddSingleton<EventHub>();
        builder.Services.AddSingleton<LogBuffer>();
        builder.Services.AddSingleton<RuntimeOperationCoordinator>();
        builder.Services.AddSingleton<V2rayRuntime>();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = RuntimeShutdownBudgets.HostShutdown);
        builder.Services.AddHostedService<V2rayHostedService>(services =>
            new V2rayHostedService(services.GetRequiredService<V2rayRuntime>()));
        builder.Services.AddSingleton(webAuth);
        builder.Services.AddSingleton<WebSessionService>();
        builder.Services.AddRateLimiter(WebAuthRateLimiting.Configure);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        var app = builder.Build();
        var runtime = app.Services.GetRequiredService<V2rayRuntime>();
        app.UseMiddleware<WebAuthRequestBodyLimitMiddleware>();
        app.UseRouting();
        app.UseRateLimiter();

        app.UseMiddleware<WebSessionAuthenticationMiddleware>();

        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (BadHttpRequestException exception) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
            {
                app.Logger.LogInformation(exception, "Invalid API request.");
                var isPayloadTooLarge = exception.StatusCode == StatusCodes.Status413PayloadTooLarge;
                context.Response.StatusCode = isPayloadTooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(ApiEnvelope<object>.Fail(
                    isPayloadTooLarge ? "payload_too_large" : "request_invalid",
                    ApiMessageKeys.CommonInvalidInput));
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
                || context.Request.Path.StartsWithSegments("/api/setup")
                || context.Request.Path.StartsWithSegments("/api/auth"))
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
                RuntimeRequestOperationKind.ExclusiveReadOnly => await operations.EnterExclusiveAsync(
                    context.RequestAborted,
                    allowReadOnlyObservations: true),
                RuntimeRequestOperationKind.Observation => await operations.EnterObservationAsync(context.RequestAborted),
                _ => await operations.EnterOperationAsync(context.RequestAborted),
            };
            context.RequestAborted = operation.Token;
            await next();
        });

        app.MapWebApi();
        app.MapWebAuthEndpoints();
        app.MapWebSetupEndpoints();
        app.MapFallback("/api/{**path}", () =>
            Results.NotFound(ApiEnvelope<object>.Fail("route_not_found", ApiMessageKeys.CommonRouteNotFound)));

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapFallbackToFile("index.html");

        if (showForegroundPrompt)
        {
            var (_, webUiUri) = GetLauncherUris(args);
            app.Lifetime.ApplicationStarted.Register(() =>
                Console.Out.WriteLine(LauncherMessages.ForegroundStarted(webUiUri.ToString(), GetLauncherLocale())));
        }

        await app.RunAsync();
        return new WebHostRunResult(0, runtime.ApplicationRestartRequested);
    }

    private static bool StartNativeRestartChild(string[] hostArguments)
    {
        var setsid = LinuxXdgBrowserOpener.FindExecutable("setsid");
        var processPath = Environment.ProcessPath;
        if (setsid is null || string.IsNullOrWhiteSpace(processPath)) return false;

        var startInfo = new System.Diagnostics.ProcessStartInfo { FileName = setsid, UseShellExecute = false };
        var commandLine = Environment.GetCommandLineArgs();
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && commandLine.Length > 0
            && commandLine[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(processPath);
            startInfo.ArgumentList.Add(commandLine[0]);
        }
        else
        {
            startInfo.ArgumentList.Add(processPath);
        }

        startInfo.ArgumentList.Add(WebLaunchOptions.BackgroundChildFlag);
        foreach (var argument in hostArguments) startInfo.ArgumentList.Add(argument);
        try
        {
            return System.Diagnostics.Process.Start(startInfo) is not null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private sealed record WebHostRunResult(int ExitCode, bool RestartRequested);

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
