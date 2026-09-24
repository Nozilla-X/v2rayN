using v2rayN.Web.Contracts;

namespace v2rayN.Web.Security;

public sealed class WebSessionAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, WebSessionService sessions)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api")
            || path == "/api/health"
            || path == "/api/setup/status"
            || path == "/api/setup"
            || path == "/api/auth/login")
        {
            await next(context);
            return;
        }

        var presentedToken = WebSessionService.ExtractPresentedToken(context);
        if (!sessions.TryValidateAndRenew(presentedToken, out var session))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                ApiEnvelope<object>.Fail("unauthorized", ApiMessageKeys.CommonUnauthorized));
            return;
        }

        if (path == "/api/events" && session is not null)
        {
            context.Items[WebSessionService.RevocationTokenContextKey] = session.RevocationToken;
        }

        await next(context);
    }
}
