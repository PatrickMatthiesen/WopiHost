using System.Security.Claims;

namespace WopiHost.Abstractions;

/// <summary>
/// Processes MS-FSSHTTP requests through a shared editing authority and conditional publication.
/// Requires an <see cref="IWopiEditingCoordinator"/> registration. Content writes must use
/// the coordinated publisher rather than a writable file stream.
/// </summary>
public interface IWopiCoordinatedCobaltProcessor
{
    /// <summary>Processes a request for an existing file using its read-only metadata.</summary>
    /// <param name="file">The existing host file.</param>
    /// <param name="principal">The authenticated user.</param>
    /// <param name="newContent">The MS-FSSHTTP request body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The MS-FSSHTTP response body.</returns>
    Task<byte[]> ProcessCobalt(IWopiFile file, ClaimsPrincipal principal, byte[] newContent,
        CancellationToken cancellationToken = default);
}
