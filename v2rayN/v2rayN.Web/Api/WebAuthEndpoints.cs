using v2rayN.Web.Contracts;
using v2rayN.Web.Security;

namespace v2rayN.Web.Api;

public static class WebAuthEndpoints
{
    public static void MapWebAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", (WebLoginRequest request, WebAuthService auth, WebSessionService sessions) =>
        {
            if (!auth.ValidateManagementKey(request.Key))
            {
                return Results.Json(
                    ApiEnvelope<object>.Fail("unauthorized", ApiMessageKeys.CommonUnauthorized),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var session = sessions.CreateSession();
            return Results.Ok(new
            {
                success = true,
                data = new { token = session.Token, expiresAt = session.ExpiresAt },
            });
        }).RequireRateLimiting(WebAuthRateLimiting.LoginPolicy);

        app.MapPost("/api/auth/logout", (HttpContext context, WebSessionService sessions) =>
        {
            sessions.Revoke(WebSessionService.ExtractPresentedToken(context));
            return Results.Ok(new { success = true });
        });
    }

    public sealed record WebLoginRequest(string? Key);
}
