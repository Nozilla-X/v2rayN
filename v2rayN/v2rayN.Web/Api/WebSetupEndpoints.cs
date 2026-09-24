using v2rayN.Web.Security;

namespace v2rayN.Web.Api;

public static class WebSetupEndpoints
{
    public static void MapWebSetupEndpoints(this WebApplication app)
    {
        app.MapGet("/api/setup/status", (WebAuthService auth) => Results.Ok(new
        {
            setupRequired = auth.SetupRequired,
            environmentKeyConfigured = auth.EnvironmentKeyConfigured,
        }));

        app.MapPost("/api/setup", async (WebSetupRequest request, HttpContext context, WebAuthService auth) =>
        {
            var result = await auth.SetupAsync(
                request.Key,
                request.ConfirmKey,
                context.Connection.RemoteIpAddress,
                context.Request.Headers.ContainsKey("Forwarded")
                    || context.Request.Headers.ContainsKey("X-Forwarded-For")
                    || context.Request.Headers.ContainsKey("X-Real-IP"),
                context.RequestAborted);

            return result switch
            {
                WebSetupResult.Created => Results.Ok(new { setupRequired = false }),
                WebSetupResult.Forbidden => Results.Json(new { error = "loopback_required" }, statusCode: StatusCodes.Status403Forbidden),
                WebSetupResult.KeyTooShort => Results.BadRequest(new { error = "key_too_short", minimumLength = WebAuthService.MinimumKeyLength }),
                WebSetupResult.KeysDoNotMatch => Results.BadRequest(new { error = "keys_do_not_match" }),
                WebSetupResult.AlreadyConfigured => Results.Conflict(new { error = "already_configured" }),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        });
    }

    public sealed record WebSetupRequest(string? Key, string? ConfirmKey);
}
