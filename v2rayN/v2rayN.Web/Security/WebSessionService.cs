using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace v2rayN.Web.Security;

public sealed class WebSessionService : IDisposable
{
    public const string RevocationTokenContextKey = "v2rayn.web.session.revocation-token";
    public static readonly TimeSpan SlidingLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(15);
    private const int TokenByteLength = 32;
    private const int TokenEncodedLength = 43;

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public WebSessionService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cleanupTimer = new Timer(
            static state => ((WebSessionService)state!).CleanupExpiredSessions(),
            this,
            CleanupInterval,
            CleanupInterval);
    }

    public WebSessionToken CreateSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            var tokenBytes = RandomNumberGenerator.GetBytes(TokenByteLength);
            var token = EncodeBase64Url(tokenBytes);
            var digest = GetDigest(tokenBytes);
            CryptographicOperations.ZeroMemory(tokenBytes);

            var now = _timeProvider.GetUtcNow();
            var absoluteExpiresAt = now + AbsoluteLifetime;
            var entry = new SessionEntry(
                now,
                now,
                Min(now + SlidingLifetime, absoluteExpiresAt),
                absoluteExpiresAt,
                new CancellationTokenSource());
            if (_sessions.TryAdd(digest, entry))
            {
                return new WebSessionToken(token, entry.ExpiresAt);
            }
            entry.Revoked.Dispose();
        }
    }

    public bool TryValidateAndRenew(string? token, out WebSessionSnapshot? session) =>
        TryValidateSessionCore(token, renew: true, out session);

    public bool TryValidateWithoutRenewal(string? token, out WebSessionSnapshot? session) =>
        TryValidateSessionCore(token, renew: false, out session);

    private bool TryValidateSessionCore(string? token, bool renew, out WebSessionSnapshot? session)
    {
        session = null;
        if (_disposed || !TryGetDigest(token, out var digest))
        {
            return false;
        }

        while (_sessions.TryGetValue(digest, out var current))
        {
            var now = _timeProvider.GetUtcNow();
            if (now >= current.ExpiresAt || now >= current.AbsoluteExpiresAt)
            {
                if (TryRemove(digest, current))
                {
                    return false;
                }
                continue;
            }

            if (!renew)
            {
                session = new WebSessionSnapshot(
                    current.CreatedAt,
                    current.LastSeenAt,
                    current.ExpiresAt,
                    current.AbsoluteExpiresAt,
                    current.Revoked.Token);
                return true;
            }

            var renewed = current with
            {
                LastSeenAt = now,
                ExpiresAt = Min(now + SlidingLifetime, current.AbsoluteExpiresAt),
            };
            if (_sessions.TryUpdate(digest, renewed, current))
            {
                session = new WebSessionSnapshot(
                    renewed.CreatedAt,
                    renewed.LastSeenAt,
                    renewed.ExpiresAt,
                    renewed.AbsoluteExpiresAt,
                    renewed.Revoked.Token);
                return true;
            }
        }

        return false;
    }

    public bool Revoke(string? token)
    {
        if (_disposed || !TryGetDigest(token, out var digest))
        {
            return false;
        }

        if (!_sessions.TryGetValue(digest, out var entry))
        {
            return false;
        }

        return TryRemove(digest, entry);
    }

    public int CleanupExpiredSessions()
    {
        if (_disposed)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        var removed = 0;
        foreach (var (digest, entry) in _sessions)
        {
            if ((now >= entry.ExpiresAt || now >= entry.AbsoluteExpiresAt)
                && TryRemove(digest, entry))
            {
                removed++;
            }
        }
        return removed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cleanupTimer.Dispose();
        foreach (var entry in _sessions.Values)
        {
            Cancel(entry.Revoked);
        }
        _sessions.Clear();
    }

    public static string ExtractPresentedToken(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization[7..].Trim();
        }

        return context.Request.Path == "/api/events"
            ? context.Request.Query["access_token"].ToString()
            : string.Empty;
    }

    private bool TryRemove(string digest, SessionEntry entry)
    {
        var removed = ((ICollection<KeyValuePair<string, SessionEntry>>)_sessions)
            .Remove(new KeyValuePair<string, SessionEntry>(digest, entry));
        if (removed)
        {
            Cancel(entry.Revoked);
        }
        return removed;
    }

    private static void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool TryGetDigest(string? token, out string digest)
    {
        digest = string.Empty;
        if (token is null || token.Length != TokenEncodedLength
            || token.Any(character => !(character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_')))
        {
            return false;
        }

        try
        {
            var base64 = token.Replace('-', '+').Replace('_', '/') + "=";
            var tokenBytes = Convert.FromBase64String(base64);
            if (tokenBytes.Length != TokenByteLength || EncodeBase64Url(tokenBytes) != token)
            {
                CryptographicOperations.ZeroMemory(tokenBytes);
                return false;
            }

            digest = GetDigest(tokenBytes);
            CryptographicOperations.ZeroMemory(tokenBytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string EncodeBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string GetDigest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first <= second ? first : second;

    private sealed record SessionEntry(
        DateTimeOffset CreatedAt,
        DateTimeOffset LastSeenAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset AbsoluteExpiresAt,
        CancellationTokenSource Revoked);
}

public sealed record WebSessionToken(string Token, DateTimeOffset ExpiresAt);

public sealed record WebSessionSnapshot(
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset AbsoluteExpiresAt,
    CancellationToken RevocationToken);
