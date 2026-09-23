namespace v2rayN.Web.Contracts;

public sealed record SubscriptionInput(
    string Remarks,
    string Url,
    string? MoreUrl = null,
    bool Enabled = true,
    string? UserAgent = null,
    string? RequestHeaders = null,
    string? Filter = null,
    int AutoUpdateInterval = 0,
    string? ConvertTarget = null,
    string? Memo = null);

public sealed record SubscriptionView(
    string Id,
    string Remarks,
    string Url,
    bool Enabled,
    int AutoUpdateInterval,
    long UpdateTime,
    string? Memo);

public sealed record ProfileView(
    string IndexId,
    string Remarks,
    string Protocol,
    string Address,
    int Port,
    string Network,
    string StreamSecurity,
    string? SubscriptionId,
    string? SubscriptionName,
    string CoreType,
    int Delay,
    decimal Speed,
    string? IpInfo,
    bool IsCurrent,
    bool CanTest);

public sealed record ListenerView(
    string Name,
    string[] Protocols,
    string ListenAddress,
    int Port,
    bool Listening);

public sealed record StatusView(
    bool CoreRunning,
    string? CoreType,
    string? CurrentProfileId,
    string? CurrentProfileName,
    DateTimeOffset? CoreStartedAt,
    ListenerView[] Listeners,
    bool XrayAvailable,
    string Runtime,
    int ProfileCount,
    int SubscriptionCount,
    DateTimeOffset CheckedAt);

public sealed record OperationView(bool Success, string Message);

public sealed record LogView(DateTimeOffset Timestamp, string Source, string Message);

public sealed record WebEvent(string Type, object Data, DateTimeOffset Timestamp);
