using System.Security.Claims;

namespace WopiHost.Abstractions;

/// <summary>Optional shared authority for base WOPI locks and another editing protocol.</summary>
/// <remarks>
/// Registration disables legacy file/container mutations in WopiHost.Core. Whole-file writes
/// require the separate IWopiCoordinatedFileMutations capability. All tokens use ordinal comparison.
/// </remarks>
public interface IWopiEditingCoordinator
{
    /// <summary>Applies one authenticated lock operation atomically, including its expected token.</summary>
    Task<WopiEditingLockResult> ProcessLockAsync(IWopiFile file, ClaimsPrincipal principal,
        WopiEditingLockOperation operation, string? token, string? replacementToken = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Supported base lock operations.</summary>
public enum WopiEditingLockOperation
{
    /// <summary>Read the current lock under authoritative coordination.</summary>
    Get,
    /// <summary>Acquire or refresh the caller's matching lock.</summary>
    Acquire,
    /// <summary>Refresh an existing lock owned by this subject and token.</summary>
    Refresh,
    /// <summary>Release only the caller's expected lock.</summary>
    Release,
    /// <summary>Atomically replace the caller's expected token.</summary>
    Replace
}

/// <summary>Result from the shared authority. A foreign protocol's lock is a conflict.</summary>
public sealed record WopiEditingLockResult(bool Success, string? CurrentLock);
