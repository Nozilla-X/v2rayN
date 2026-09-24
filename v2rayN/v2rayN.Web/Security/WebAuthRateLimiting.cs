using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace v2rayN.Web.Security;

public static class WebAuthRateLimiting
{
    public const string LoginPolicy = "web-auth-login";
    public const string SetupPolicy = "web-auth-setup";

    public static void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(LoginPolicy, context => CreateIpPartition(context, permitLimit: 5));
        options.AddPolicy(SetupPolicy, context => CreateIpPartition(context, permitLimit: 10));
    }

    private static RateLimitPartition<string> CreateIpPartition(HttpContext context, int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = permitLimit,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                Window = TimeSpan.FromMinutes(1),
            });
}
