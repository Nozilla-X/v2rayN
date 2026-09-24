namespace v2rayN.Web.Launcher;

public enum WebLaunchMode
{
    Foreground,
    BackgroundLauncher,
    BackgroundChild,
}

public sealed record WebLaunchOptions(WebLaunchMode Mode, bool NoOpen, string[] HostArguments)
{
    public const string ForegroundFlag = "--foreground";
    public const string BackgroundFlag = "--background";
    public const string NoOpenFlag = "--no-open";
    public const string BackgroundChildFlag = "--background-child";

    public static WebLaunchOptions Parse(
        string[] arguments,
        bool isLinux,
        bool daemonEnvironment,
        bool containerEnvironment)
    {
        var mode = arguments.Contains(BackgroundChildFlag, StringComparer.Ordinal)
            ? WebLaunchMode.BackgroundChild
            : arguments.Contains(ForegroundFlag, StringComparer.Ordinal)
                ? WebLaunchMode.Foreground
                : arguments.Contains(BackgroundFlag, StringComparer.Ordinal)
                    ? WebLaunchMode.BackgroundLauncher
                    : isLinux && !daemonEnvironment && !containerEnvironment
                        ? WebLaunchMode.BackgroundLauncher
                        : WebLaunchMode.Foreground;

        var hostArguments = arguments
            .Where(argument => argument is not ForegroundFlag
                and not BackgroundFlag
                and not NoOpenFlag
                and not BackgroundChildFlag)
            .ToArray();
        return new WebLaunchOptions(
            mode,
            arguments.Contains(NoOpenFlag, StringComparer.Ordinal),
            hostArguments);
    }
}

public static class LauncherEnvironment
{
    public static bool IsDaemonEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        return HasValue(environment, "INVOCATION_ID")
            || HasValue(environment, "JOURNAL_STREAM")
            || HasValue(environment, "NOTIFY_SOCKET");
    }

    public static bool IsContainerEnvironment(IReadOnlyDictionary<string, string?> environment) =>
        HasValue(environment, "DOTNET_RUNNING_IN_CONTAINER")
        || HasValue(environment, "container");

    private static bool HasValue(IReadOnlyDictionary<string, string?> environment, string key) =>
        environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
}

public enum LauncherLocale
{
    SimplifiedChinese,
    TraditionalChinese,
    English,
}

public static class LauncherMessages
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> MessageCache = new(StringComparer.Ordinal);

    public static LauncherLocale ResolveLocale(params string?[] localeValues)
    {
        var locale = localeValues.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        var normalized = locale.Replace('_', '-');
        if (normalized.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase))
        {
            return LauncherLocale.TraditionalChinese;
        }

        return normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? LauncherLocale.SimplifiedChinese
            : LauncherLocale.English;
    }

    public static string Started(string url, LauncherLocale locale) => Format("started", locale, url);

    public static string AlreadyRunning(string url, bool browserOpened, LauncherLocale locale) =>
        Format(browserOpened ? "alreadyRunningOpening" : "alreadyRunning", locale, url);

    public static string StartFailed(LauncherLocale locale) => Get("startFailed", locale);

    public static string ExistingUnhealthy(LauncherLocale locale) => Get("existingUnhealthy", locale);

    public static string InstanceInUse(LauncherLocale locale) => Get("instanceInUse", locale);

    private static string Format(string key, LauncherLocale locale, string url) =>
        Get(key, locale).Replace("{url}", url, StringComparison.Ordinal);

    private static string Get(string key, LauncherLocale locale)
    {
        var localeName = locale switch
        {
            LauncherLocale.SimplifiedChinese => "zh-CN",
            LauncherLocale.TraditionalChinese => "zh-TW",
            _ => "en-US",
        };
        return MessageCache.GetOrAdd($"{localeName}:{key}", _ => ReadMessage(localeName, key));
    }

    private static string ReadMessage(string localeName, string key)
    {
        var resourceName = $"v2rayN.Web.Locales.{localeName}.json";
        using var stream = typeof(LauncherMessages).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded locale resource: {resourceName}");
        using var document = System.Text.Json.JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("launcher").GetProperty(key).GetString()
            ?? throw new InvalidOperationException($"Missing launcher locale key: {localeName}.{key}");
    }
}
