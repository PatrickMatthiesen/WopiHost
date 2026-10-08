using CellBridge.Storage.Abstractions;
using WopiHost.Abstractions;

namespace WopiHost.CellBridge;

/// <summary>Adapts the host's conditional revision store without using unconditional write streams.</summary>
public sealed class WopiRevisionDestination(IWopiRevisionStore store) : IExternalRevisionDestination
{
    public async ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request,
        Stream verifiedContent, CancellationToken cancellationToken = default)
    {
        var revision = request.Revision;
        var result = await store.CompareExchangeAsync(new(request.Destination, request.ExpectedRevision,
            request.BindingId, revision.OperationId, revision.ResourceId, revision.LifecycleGeneration,
            revision.Sequence, revision.ContentVersion, revision.Content.Length, revision.Content.Sha256),
            verifiedContent, cancellationToken).ConfigureAwait(false);
        if (result.Status == WopiRevisionWriteStatus.Applied &&
            (string.IsNullOrWhiteSpace(result.Revision) || result.Revision == request.ExpectedRevision))
            throw new InvalidDataException("The destination did not return a new revision receipt.");
        return result.Status switch
        {
            WopiRevisionWriteStatus.Applied => new(ExternalDeliveryStatus.Applied, result.Revision),
            WopiRevisionWriteStatus.Conflict => new(ExternalDeliveryStatus.Conflict),
            WopiRevisionWriteStatus.Stale => new(ExternalDeliveryStatus.Stale),
            _ => throw new InvalidDataException("Unknown destination delivery status.")
        };
    }
}
