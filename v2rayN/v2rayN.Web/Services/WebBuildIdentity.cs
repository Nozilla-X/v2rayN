using System.Reflection;
using System.Runtime.InteropServices;

namespace v2rayN.Web.Services;

public sealed record WebBuildIdentity(string Version, string Commit, string BuildDate, string Rid)
{
    public static WebBuildIdentity Current { get; } = Load();

    private static WebBuildIdentity Load()
    {
        var metadata = Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(item => item.Key is "WebVersion" or "WebCommit" or "WebBuildDate")
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        return new WebBuildIdentity(
            metadata.GetValueOrDefault("WebVersion") ?? "7.25.2-web.0",
            metadata.GetValueOrDefault("WebCommit") ?? "unknown",
            metadata.GetValueOrDefault("WebBuildDate") ?? "unknown",
            RuntimeInformation.RuntimeIdentifier);
    }
}
