using v2rayN.Web.Contracts;

namespace v2rayN.Web.Services;

internal sealed record RestoreRuntimeIntent(bool WasRunning, string? PreferredProfileId);

internal enum RestoreRuntimeRecoverySource
{
    NotRequested,
    PreferredProfile,
    RestoredSelection,
    DefaultProfile,
    NoProfile,
    StartFailed,
}

internal sealed record RestoreRuntimeRecoveryResult(
    RestoreRuntimeRecoverySource Source,
    string? ProfileId = null,
    string? FailureCode = null);

internal static class RestoreRuntimeRecovery
{
    public static bool ShouldAutoStart(RestoreRuntimeIntent? intent, bool configured) =>
        intent is null && configured;

    public static async Task<RestoreRuntimeRecoveryResult> RecoverAsync(
        RestoreRuntimeIntent intent,
        string? restoredSelectedProfileId,
        string? defaultProfileId,
        Func<string, CancellationToken, Task<bool>> profileExists,
        Func<string, CancellationToken, Task<OperationView>> startCore,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (!intent.WasRunning)
        {
            return new(RestoreRuntimeRecoverySource.NotRequested);
        }

        log("Restore requested Core runtime recovery.");
        var preferredId = Normalize(intent.PreferredProfileId);
        if (preferredId is not null && await profileExists(preferredId, cancellationToken))
        {
            return await StartAsync(preferredId, RestoreRuntimeRecoverySource.PreferredProfile, startCore, log, cancellationToken);
        }

        if (preferredId is not null)
        {
            log($"Previous profile {preferredId} no longer exists after restore.");
        }

        var selectedId = Normalize(restoredSelectedProfileId);
        if (selectedId is not null && selectedId != preferredId)
        {
            if (await profileExists(selectedId, cancellationToken))
            {
                log($"Falling back to restored selected profile {selectedId}.");
                return await StartAsync(selectedId, RestoreRuntimeRecoverySource.RestoredSelection, startCore, log, cancellationToken);
            }

            log($"Restored selected profile {selectedId} does not exist after restore.");
        }

        var fallbackId = Normalize(defaultProfileId);
        if (fallbackId is not null && fallbackId != preferredId && fallbackId != selectedId
            && await profileExists(fallbackId, cancellationToken))
        {
            log($"Falling back to the ServiceLib default profile {fallbackId}.");
            return await StartAsync(fallbackId, RestoreRuntimeRecoverySource.DefaultProfile, startCore, log, cancellationToken);
        }

        log("Restore requested Core runtime recovery, but no valid profile exists in the restored configuration. Core remains stopped.");
        return new(RestoreRuntimeRecoverySource.NoProfile);
    }

    private static async Task<RestoreRuntimeRecoveryResult> StartAsync(
        string profileId,
        RestoreRuntimeRecoverySource source,
        Func<string, CancellationToken, Task<OperationView>> startCore,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var result = await startCore(profileId, cancellationToken);
        if (result.Success)
        {
            log($"Core restored successfully using {profileId}.");
            return new(source, profileId);
        }

        log($"Restore requested Core runtime recovery, but Core failed to start using {profileId} (code: {result.Code}). No further restart will be attempted.");
        return new(RestoreRuntimeRecoverySource.StartFailed, profileId, result.Code);
    }

    private static string? Normalize(string? profileId) =>
        string.IsNullOrWhiteSpace(profileId) ? null : profileId;
}
