using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WopiHost.Abstractions;

namespace WopiHost.Core.Infrastructure;

/// <summary>
/// Short-circuits the request with <c>501 Not Implemented</c> when no
/// <see cref="IWopiWritableStorageProvider"/> is registered. Attached to endpoints that mutate
/// storage (PUT, DELETE, RENAME, …) so read-only hosts surface the unimplemented capability the
/// way the WOPI spec mandates.
/// </summary>
internal sealed class RequiresWritableStorageEndpointFilter : IEndpointFilter
{
    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.RequestServices.GetService<IWopiEditingCoordinator>() is not null)
        {
            var endpoint = http.GetEndpoint();
            var operations = endpoint?.Metadata.GetMetadata<WopiOverrideMetadata>()?.Values;
            if ((endpoint?.Metadata.GetMetadata<WopiPutFileMetadata>() is not null &&
                http.RequestServices.GetService<IWopiCoordinatedFileMutations>() is not null) ||
                operations is not null && operations.All(op => op is WopiFileOperations.Lock or
                    WopiFileOperations.Put or WopiFileOperations.Unlock or WopiFileOperations.RefreshLock or WopiFileOperations.GetLock))
                return next(context);
        }
        if (http.RequestServices.GetService<IWopiWritableStorageProvider>() is null)
        {
            return new ValueTask<object?>(TypedResults.StatusCode(StatusCodes.Status501NotImplemented));
        }
        return next(context);
    }
}
