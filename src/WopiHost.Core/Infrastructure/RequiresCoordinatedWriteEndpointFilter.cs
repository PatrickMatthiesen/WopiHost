using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WopiHost.Abstractions;
using WopiHost.Core.Results;

namespace WopiHost.Core.Infrastructure;

/// <summary>Prevents legacy stream mutations from bypassing a registered shared editing authority.</summary>
internal sealed class RequiresCoordinatedWriteEndpointFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.RequestServices.GetService<IWopiEditingCoordinator>() is null) return next(context);
        var endpoint = http.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<WopiPutFileMetadata>() is not null &&
            http.RequestServices.GetService<IWopiCoordinatedFileMutations>() is not null)
            return InvokeCoordinatedAsync(context, next);
        var operations = endpoint?.Metadata.GetMetadata<WopiOverrideMetadata>()?.Values;
        // Use selected endpoint metadata, not a spoofable header on a PutFile route.
        if (operations is not null && operations.All(op => op is WopiFileOperations.Cobalt or
            WopiFileOperations.Lock or WopiFileOperations.Put or WopiFileOperations.Unlock or
            WopiFileOperations.RefreshLock or WopiFileOperations.GetLock)) return InvokeCoordinatedAsync(context, next);
        http.Response.Headers[WopiHeaders.MutationFailureReason] = operations?.FirstOrDefault() switch
        {
            WopiFileOperations.PutRelativeFile => "Coordinated relative file creation and overwrite are unavailable.",
            WopiFileOperations.RenameFile => "Coordinated file rename requires atomic catalog and protocol path updates.",
            WopiFileOperations.DeleteFile => "Coordinated file deletion requires tombstone and publication retirement.",
            WopiContainerOperations.CreateChildFile => "Coordinated child file creation requires atomic namespace reservation and binding.",
            WopiContainerOperations.CreateChildContainer => "Coordinated child container creation requires namespace coordination.",
            WopiContainerOperations.RenameContainer => "Coordinated container rename requires atomic subtree path updates.",
            WopiContainerOperations.DeleteContainer => "Coordinated container deletion requires atomic emptiness and tombstone checks.",
            _ => "Coordinated whole-file saving is unavailable."
        };
        return ValueTask.FromResult<object?>(TypedResults.StatusCode(StatusCodes.Status501NotImplemented));
    }
    private static async ValueTask<object?> InvokeCoordinatedAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context).ConfigureAwait(false); }
        catch (UnauthorizedAccessException) { return TypedResults.StatusCode(StatusCodes.Status403Forbidden); }
        catch (WopiResourceLockedException ex) { return new WopiLockMismatchResult(ex.LockId); }
    }
}
