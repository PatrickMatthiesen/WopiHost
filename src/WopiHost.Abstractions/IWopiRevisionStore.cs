namespace WopiHost.Abstractions;

/// <summary>Optional storage capability for conditional, recoverable revision delivery.</summary>
/// <remarks>
/// Read returns one immutable snapshot and its revision together. CompareExchange atomically
/// checks the revision and commits bytes, a never-reused revision token, and a durable receipt
/// for the complete request fingerprint. All other writers must advance the same revision.
/// Receipts keyed by binding/operation survive restart, deletion, recreation and rebinding.
/// Repeating an identical operation returns its original result without writing again, even
/// after later edits. A changed fingerprint must be rejected. Compacted receipts return Stale.
/// A version check followed by OpenWriteAsync does not implement this contract.
/// </remarks>
public interface IWopiRevisionStore
{
    /// <summary>Reads an existing file's coherent baseline. The caller owns the returned stream.</summary>
    Task<WopiRevisionRead> ReadAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>Conditionally commits verified bytes and their durable receipt as one operation.</summary>
    Task<WopiRevisionWriteResult> CompareExchangeAsync(WopiRevisionWrite request, Stream content,
        CancellationToken cancellationToken = default);
}

/// <summary>Immutable file bytes and their corresponding opaque revision.</summary>
public sealed record WopiRevisionRead(Stream Content, string Revision, long Length, string Sha256);

/// <summary>Complete immutable delivery fingerprint. File IDs are opaque and case-sensitive.</summary>
public sealed record WopiRevisionWrite(string FileId, string ExpectedRevision, Guid BindingId,
    Guid OperationId, Guid ResourceId, long LifecycleGeneration, long Sequence, uint ContentVersion,
    long Length, string Sha256);

/// <summary>Conditional delivery outcome.</summary>
public enum WopiRevisionWriteStatus
{
    /// <summary>Committed or recovered from an identical durable receipt.</summary>
    Applied,
    /// <summary>The destination no longer has the expected revision.</summary>
    Conflict,
    /// <summary>The operation is retired and must never be applied again.</summary>
    Stale
}

/// <summary>Applied returns the original resulting revision on receipt replay.</summary>
public sealed record WopiRevisionWriteResult(WopiRevisionWriteStatus Status, string? Revision = null);
