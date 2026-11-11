using WopiHost.Abstractions;

namespace WopiHost.Core.Infrastructure;

/// <summary>Rejects namespace writes when the host has no writable storage provider.</summary>
internal sealed class UnavailableNewChildFileNegotiator : IWopiNewChildFileNegotiator
{
    public Task<WopiNewChildFileResult> NegotiateAsync(WopiNewChildFileRequest request,
        CancellationToken cancellationToken = default) => Task.FromResult(WopiNewChildFileResult.NotImplemented());
}
