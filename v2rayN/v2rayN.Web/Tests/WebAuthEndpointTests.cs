using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using v2rayN.Web.Api;
using v2rayN.Web.Security;
using v2rayN.Web.Services;

namespace v2rayN.Web.Tests;

public class WebAuthEndpointTests
{
    private const string ManagementKey = "test-management-key-2026";

    [Test]
    public async Task EnvironmentManagementKeyLogsInAndOnlyTheReturnedSessionAuthenticates()
    {
        using var directory = new TemporaryDirectory();
        using var sessions = new WebSessionService();
        await using var api = await ApiHarness.StartAsync(
            new WebAuthService(Path.Combine(directory.Path, "web-auth.json"), ManagementKey),
            sessions);

        using var invalidLogin = await api.Client.PostAsJsonAsync("/api/auth/login", new { key = "wrong-management-key" });
        await (invalidLogin.StatusCode == HttpStatusCode.Unauthorized).Should().BeTrue();
        using var validLogin = await api.Client.PostAsJsonAsync("/api/auth/login", new { key = ManagementKey });
        await (validLogin.StatusCode == HttpStatusCode.OK).Should().BeTrue();
        using var loginDocument = JsonDocument.Parse(await validLogin.Content.ReadAsStringAsync());
        var sessionToken = loginDocument.RootElement.GetProperty("data").GetProperty("token").GetString()!;
        await (sessionToken.Length == 43).Should().BeTrue();
        await (loginDocument.RootElement.GetProperty("data").GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow).Should().BeTrue();

        using var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/api/test/session");
        sessionRequest.Headers.Authorization = new("Bearer", sessionToken);
        using var sessionResponse = await api.Client.SendAsync(sessionRequest);
        await (sessionResponse.StatusCode == HttpStatusCode.OK).Should().BeTrue();

        using var managementKeyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/test/session");
        managementKeyRequest.Headers.Authorization = new("Bearer", ManagementKey);
        using var managementKeyResponse = await api.Client.SendAsync(managementKeyRequest);
        await (managementKeyResponse.StatusCode == HttpStatusCode.Unauthorized).Should().BeTrue();

        using var managementKeySse = await api.Client.GetAsync(
            $"/api/events?access_token={Uri.EscapeDataString(ManagementKey)}",
            HttpCompletionOption.ResponseHeadersRead);
        await (managementKeySse.StatusCode == HttpStatusCode.Unauthorized).Should().BeTrue();
    }

    [Test]
    public async Task PersistedManagementKeyExchangesForSessionAtLogin()
    {
        using var directory = new TemporaryDirectory();
        var configPath = Path.Combine(directory.Path, "guiConfigs", "web-auth.json");
        var seedAuth = new WebAuthService(configPath, null);
        await ((await seedAuth.SetupAsync(ManagementKey, ManagementKey, setupAccessAllowed: true)) == WebSetupResult.Created).Should().BeTrue();

        using var sessions = new WebSessionService();
        await using var api = await ApiHarness.StartAsync(new WebAuthService(configPath, null), sessions);

        using var response = await api.Client.PostAsJsonAsync("/api/auth/login", new { key = ManagementKey });
        await (response.StatusCode == HttpStatusCode.OK).Should().BeTrue();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = document.RootElement.GetProperty("data").GetProperty("token").GetString()!;
        await sessions.TryValidate(token, out _).Should().BeTrue();
    }

    [Test]
    public async Task SetupRequiresLocalAddressAndAllowedHostAndReturnsAUsableSession()
    {
        using var directory = new TemporaryDirectory();
        var configPath = Path.Combine(directory.Path, "guiConfigs", "web-auth.json");
        using var sessions = new WebSessionService();
        await using var api = await ApiHarness.StartAsync(new WebAuthService(configPath, null), sessions);

        using var status = await api.Client.GetAsync("/api/setup/status");
        using var statusDocument = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        await statusDocument.RootElement.GetProperty("setupRequired").GetBoolean().Should().BeTrue();
        await statusDocument.RootElement.GetProperty("setupAllowedFromThisRequest").GetBoolean().Should().BeTrue();

        using var maliciousHostRequest = new HttpRequestMessage(HttpMethod.Get, "/api/setup/status");
        maliciousHostRequest.Headers.Host = "attacker.example";
        using var maliciousHostStatus = await api.Client.SendAsync(maliciousHostRequest);
        using var maliciousStatusDocument = JsonDocument.Parse(await maliciousHostStatus.Content.ReadAsStringAsync());
        await maliciousStatusDocument.RootElement.GetProperty("setupAllowedFromThisRequest").GetBoolean().Should().BeFalse();

        using var deniedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/setup")
        {
            Content = JsonContent.Create(new { key = ManagementKey, confirmKey = ManagementKey }),
        };
        deniedRequest.Headers.Host = "attacker.example";
        using var deniedResponse = await api.Client.SendAsync(deniedRequest);
        await (deniedResponse.StatusCode == HttpStatusCode.Forbidden).Should().BeTrue();

        using var setupResponse = await api.Client.PostAsJsonAsync("/api/setup", new { key = ManagementKey, confirmKey = ManagementKey });
        await (setupResponse.StatusCode == HttpStatusCode.OK).Should().BeTrue();
        using var setupDocument = JsonDocument.Parse(await setupResponse.Content.ReadAsStringAsync());
        await setupDocument.RootElement.GetProperty("setupRequired").GetBoolean().Should().BeFalse();
        var sessionToken = setupDocument.RootElement.GetProperty("token").GetString()!;
        await sessions.TryValidate(sessionToken, out _).Should().BeTrue();

        using var protectedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/test/session");
        protectedRequest.Headers.Authorization = new("Bearer", sessionToken);
        using var protectedResponse = await api.Client.SendAsync(protectedRequest);
        await (protectedResponse.StatusCode == HttpStatusCode.OK).Should().BeTrue();
        var persistedConfig = await File.ReadAllTextAsync(configPath);
        await persistedConfig.Contains(ManagementKey, StringComparison.Ordinal).Should().BeFalse();
    }

    [Test]
    public async Task LogoutRevokesOnlyTheCurrentSession()
    {
        using var directory = new TemporaryDirectory();
        using var sessions = new WebSessionService();
        await using var api = await ApiHarness.StartAsync(
            new WebAuthService(Path.Combine(directory.Path, "web-auth.json"), ManagementKey),
            sessions);
        var first = sessions.CreateSession();
        var second = sessions.CreateSession();

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Authorization = new("Bearer", first.Token);
        using var logoutResponse = await api.Client.SendAsync(logout);
        await (logoutResponse.StatusCode == HttpStatusCode.OK).Should().BeTrue();

        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, "/api/test/session");
        firstRequest.Headers.Authorization = new("Bearer", first.Token);
        using var firstResponse = await api.Client.SendAsync(firstRequest);
        await (firstResponse.StatusCode == HttpStatusCode.Unauthorized).Should().BeTrue();

        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, "/api/test/session");
        secondRequest.Headers.Authorization = new("Bearer", second.Token);
        using var secondResponse = await api.Client.SendAsync(secondRequest);
        await (secondResponse.StatusCode == HttpStatusCode.OK).Should().BeTrue();
    }

    [Test]
    public async Task RevokingAnActiveSseSessionClosesItsStream()
    {
        using var directory = new TemporaryDirectory();
        using var sessions = new WebSessionService();
        await using var api = await ApiHarness.StartAsync(
            new WebAuthService(Path.Combine(directory.Path, "web-auth.json"), ManagementKey),
            sessions);
        var session = sessions.CreateSession();

        var responseTask = api.Client.GetAsync(
            $"/api/events?access_token={Uri.EscapeDataString(session.Token)}",
            HttpCompletionOption.ResponseHeadersRead);
        for (var attempt = 0; attempt < 100 && !responseTask.IsCompleted; attempt++)
        {
            api.Events.Publish("status", new { ready = true });
            await Task.Delay(10);
        }

        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(3));
        await (response.StatusCode == HttpStatusCode.OK).Should().BeTrue();
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[512];
        var firstRead = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await (firstRead > 0).Should().BeTrue();

        sessions.Revoke(session.Token);
        var closed = false;
        try
        {
            closed = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) == 0;
        }
        catch (IOException)
        {
            closed = true;
        }
        await closed.Should().BeTrue();
    }

    [Test]
    public async Task ExpiringAnActiveSseSessionClosesItsStream()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-04-01T00:00:00Z"));
        using var sessions = new WebSessionService(time);
        await using var api = await ApiHarness.StartAsync(
            new WebAuthService(Path.Combine(directory.Path, "web-auth.json"), ManagementKey),
            sessions);
        var session = sessions.CreateSession();
        var responseTask = api.Client.GetAsync(
            $"/api/events?access_token={Uri.EscapeDataString(session.Token)}",
            HttpCompletionOption.ResponseHeadersRead);
        for (var attempt = 0; attempt < 100 && !responseTask.IsCompleted; attempt++)
        {
            api.Events.Publish("status", new { ready = true });
            await Task.Delay(10);
        }

        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(3));
        await (response.StatusCode == HttpStatusCode.OK).Should().BeTrue();
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[512];
        await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        time.Advance(WebSessionService.SlidingLifetime + TimeSpan.FromSeconds(1));
        await sessions.TryValidate(session.Token, out _).Should().BeFalse();
        var closed = false;
        try
        {
            closed = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) == 0;
        }
        catch (IOException)
        {
            closed = true;
        }
        await closed.Should().BeTrue();
    }

    private sealed class ApiHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ApiHarness(WebApplication app, HttpClient client, EventHub events)
        {
            _app = app;
            Client = client;
            Events = events;
        }

        public HttpClient Client { get; }
        public EventHub Events { get; }

        public static async Task<ApiHarness> StartAsync(WebAuthService auth, WebSessionService sessions)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
            builder.Services.AddSingleton(auth);
            builder.Services.AddSingleton(sessions);
            builder.Services.AddSingleton<EventHub>();
            builder.Services.AddSingleton<LogBuffer>();
            builder.Services.AddSingleton<RuntimeOperationCoordinator>();
            builder.Services.AddSingleton<V2rayRuntime>();
            builder.Services.AddRateLimiter(WebAuthRateLimiting.Configure);

            var app = builder.Build();
            app.UseRouting();
            app.UseRateLimiter();
            app.UseMiddleware<WebSessionAuthenticationMiddleware>();
            app.MapWebApi();
            app.MapWebAuthEndpoints();
            app.MapWebSetupEndpoints();
            app.MapGet("/api/test/session", () => Results.Ok(new { authorized = true }));
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                BaseAddress = new Uri(addresses.Single()),
                Timeout = TimeSpan.FromSeconds(5),
            };
            return new ApiHarness(app, client, app.Services.GetRequiredService<EventHub>());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"v2rayn-web-auth-api-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _now = initialTime;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
