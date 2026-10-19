using System.Security.Claims;

namespace WopiHost.Abstractions;

/// <summary>Optional whole-file mutation capability using the registered shared editing authority.</summary>
/// <remarks>
/// Implementations must commit protocol content, graphs, history and publication work together.
/// They must never fall back to unconditional write streams. Applied means the destination has
/// acknowledged the accepted revision. Operation identities are scoped to the file and subject;
/// repeating an identity with changed bytes must fail without changing content.
/// </remarks>
public interface IWopiCoordinatedFileMutations
{
    /// <summary>Whether this file's format supports graph-aware whole-file replacement.</summary>
    bool SupportsPutFile(IWopiFile file);

    /// <summary>Accepts a bounded whole-file save and attempts its ordered conditional delivery.</summary>
    Task<WopiCoordinatedPutFileResult> PutFileAsync(IWopiFile file, ClaimsPrincipal principal,
        Stream content, Guid operationId, string? lockToken = null, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a coordinated whole-file save and its destination publication.</summary>
public enum WopiCoordinatedPutFileStatus
{
    /// <summary>The accepted revision was acknowledged by the destination.</summary>
    Applied,
    /// <summary>The durable accepted revision is waiting for destination acknowledgement.</summary>
    Pending,
    /// <summary>The operation or destination conflicts with authoritative state.</summary>
    Conflict,
    /// <summary>The request body is not a supported valid document.</summary>
    InvalidContent,
    /// <summary>The request exceeds the configured storage limits.</summary>
    TooLarge
}

/// <summary>Applied includes the destination revision; a replay does not create another local revision.</summary>
public sealed record WopiCoordinatedPutFileResult(WopiCoordinatedPutFileStatus Status,
    string? Version = null, bool IsReplay = false);
