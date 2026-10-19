using System.Collections.ObjectModel;
using System.Security.Claims;

namespace WopiHost.Abstractions;

/// <summary>
/// Context for a <c>PutFile</c> request after file-store confirmation and before its HTTP response.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Editors"/> collection is parsed from the optional <c>X-WOPI-Editors</c>
/// request header per the WOPI <see href="https://learn.microsoft.com/microsoft-365/cloud-storage-partner-program/rest/files/putfile">PutFile</see>
/// spec: a comma-delimited list of <see cref="WopiCheckFileInfo.UserId"/> values for every
/// user who contributed to the changes flushed in this PutFile request. Useful for audit
/// trails and last-touch metadata. The list is empty when the header is absent or empty.
/// </para>
/// <para>
/// The callback runs after storage confirms the save but before the controller
/// emits the 200 response. Throwing here turns the response into a 500 — for non-fatal
/// bookkeeping (audit log, last-edit timestamp), swallow exceptions inside the handler.
/// </para>
/// <para>
/// Confirmed retries invoke the callback again even when the file write is deduplicated.
/// For coordinated saves, use <see cref="OperationId"/> with the file and user identity
/// to recognize duplicate bookkeeping work. Callback delivery is not a durable exactly-once operation.
/// </para>
/// </remarks>
/// <param name="User">the principal that issued the PutFile request.</param>
/// <param name="File">the current file resource after save acknowledgement.</param>
/// <param name="Editors">user ids who contributed to this PutFile, parsed from
/// <c>X-WOPI-Editors</c>. Empty when the header is absent.</param>
public record WopiPutFileContext(
    ClaimsPrincipal? User,
    IWopiFile File,
    ReadOnlyCollection<string> Editors)
{
    /// <summary>
    /// The supplied or generated coordinated save ID. Clients reusing an operation ID
    /// receive the same ID in this callback. Null for legacy writes.
    /// Scope this ID to the file and user when deduplicating callback work.
    /// </summary>
    public Guid? OperationId { get; init; }
}
