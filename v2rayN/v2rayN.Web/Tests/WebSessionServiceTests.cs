using Microsoft.AspNetCore.Http;
using v2rayN.Web.Security;

namespace v2rayN.Web.Tests;

public class WebSessionServiceTests
{
    [Test]
    public async Task SessionTokensAreRandomUrlSafeAndManagementKeysAreNotSessions()
    {
        using var sessions = new WebSessionService();
        var first = sessions.CreateSession();
        var second = sessions.CreateSession();
        const string managementKey = "this-is-a-management-key";

        await (first.Token.Length == 43).Should().BeTrue();
        await first.Token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').Should().BeTrue();
        await (first.Token != second.Token).Should().BeTrue();
        await sessions.TryValidate(first.Token, out _).Should().BeTrue();
        await sessions.TryValidate(managementKey, out _).Should().BeFalse();
    }

    [Test]
    public async Task SessionsSlideForSevenDaysAndExpireAfterIdleTimeout()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using var sessions = new WebSessionService(time);
        var created = sessions.CreateSession();

        time.Advance(TimeSpan.FromDays(6));
        await sessions.TryValidate(created.Token, out var renewed).Should().BeTrue();
        await (renewed!.LastSeenAt == time.GetUtcNow()).Should().BeTrue();
        await (renewed.ExpiresAt == time.GetUtcNow() + WebSessionService.SlidingLifetime).Should().BeTrue();

        time.Advance(TimeSpan.FromDays(8));
        await sessions.TryValidate(created.Token, out _).Should().BeFalse();
    }

    [Test]
    public async Task SessionHasThirtyDayAbsoluteLifetimeEvenWhenActive()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-02-01T00:00:00Z"));
        using var sessions = new WebSessionService(time);
        var startedAt = time.GetUtcNow();
        var created = sessions.CreateSession();

        for (var day = 0; day < 5; day++)
        {
            time.Advance(TimeSpan.FromDays(5));
            await sessions.TryValidate(created.Token, out _).Should().BeTrue();
        }

        await (time.GetUtcNow() == startedAt + TimeSpan.FromDays(25)).Should().BeTrue();
        await sessions.TryValidate(created.Token, out var active).Should().BeTrue();
        await (active!.AbsoluteExpiresAt == startedAt + WebSessionService.AbsoluteLifetime).Should().BeTrue();
        time.Advance(TimeSpan.FromDays(5));
        await sessions.TryValidate(created.Token, out _).Should().BeFalse();
    }

    [Test]
    public async Task RevocationCancelsActiveConnectionsAndOnlyRevokesThatSession()
    {
        using var sessions = new WebSessionService();
        var first = sessions.CreateSession();
        var second = sessions.CreateSession();
        await sessions.TryValidate(first.Token, out var activeSession).Should().BeTrue();

        await sessions.Revoke(first.Token).Should().BeTrue();
        await activeSession!.RevocationToken.IsCancellationRequested.Should().BeTrue();
        await sessions.TryValidate(first.Token, out _).Should().BeFalse();
        await sessions.TryValidate(second.Token, out _).Should().BeTrue();
    }

    [Test]
    public async Task SseReadsOnlyItsSessionTokenAndBearerTakesPrecedence()
    {
        using var sessions = new WebSessionService();
        var session = sessions.CreateSession();
        const string managementKey = "management-key-never-for-sse";
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/events";
        context.Request.QueryString = new QueryString($"?access_token={Uri.EscapeDataString(session.Token)}");

        var queryToken = WebSessionService.ExtractPresentedToken(context);
        await (queryToken == session.Token).Should().BeTrue();
        await sessions.TryValidate(queryToken, out _).Should().BeTrue();

        context.Request.Headers.Authorization = $"Bearer {managementKey}";
        await (WebSessionService.ExtractPresentedToken(context) == managementKey).Should().BeTrue();
        await sessions.TryValidate(WebSessionService.ExtractPresentedToken(context), out _).Should().BeFalse();
    }

    [Test]
    public async Task CleanupRemovesExpiredSessions()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-03-01T00:00:00Z"));
        using var sessions = new WebSessionService(time);
        var session = sessions.CreateSession();
        await sessions.TryValidate(session.Token, out var active).Should().BeTrue();

        time.Advance(WebSessionService.SlidingLifetime + TimeSpan.FromSeconds(1));
        await (sessions.CleanupExpiredSessions() == 1).Should().BeTrue();
        await active!.RevocationToken.IsCancellationRequested.Should().BeTrue();
        await sessions.TryValidate(session.Token, out _).Should().BeFalse();
    }

    [Test]
    public async Task SessionsDoNotSurviveServiceRestart()
    {
        var firstProcess = new WebSessionService();
        var session = firstProcess.CreateSession();
        firstProcess.Dispose();

        using var restartedProcess = new WebSessionService();
        await restartedProcess.TryValidate(session.Token, out _).Should().BeFalse();
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _now = initialTime;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
