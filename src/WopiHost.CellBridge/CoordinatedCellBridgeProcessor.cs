using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Options;
using WopiHost.Abstractions;

namespace WopiHost.CellBridge;

/// <summary>Opt-in adapter using shared protocol authority and conditional external publishing.</summary>
public sealed class CoordinatedCellBridgeProcessor : ICobaltProcessor, IWopiEditingCoordinator
{
    private readonly CellBridgeDocumentService _documents;
    private readonly CellBridgeRequestProcessor _requests;
    private readonly ExternalRevisionPublisher _publisher;
    private readonly ExternalRevisionPublicationWorker _worker;
    private readonly IWopiRevisionStore _destination;
    private readonly IWopiPermissionProvider _permissions;
    private readonly CoordinatedCellBridgeOptions _options;

    public CoordinatedCellBridgeProcessor(CellBridgeDocumentService documents, CellBridgeRequestProcessor requests,
        ExternalRevisionPublisher publisher, ExternalRevisionPublicationWorker worker, IWopiRevisionStore destination,
        IWopiPermissionProvider permissions, ICellBridgeAuthorizationPolicy policy,
        IOptions<CoordinatedCellBridgeOptions> options)
    {
        _options = options.Value;
        _options.Validate();
        if (string.IsNullOrWhiteSpace(policy.PolicyDomain))
            throw new InvalidOperationException("Coordinated editing requires a host-owned authorization policy.");
        if (!_options.AllowVolatileProtocolState && !documents.Provider.Capabilities.Durable)
            throw new InvalidOperationException("Coordinated editing requires durable protocol state and content.");
        _documents = documents;
        _requests = requests;
        _publisher = publisher;
        _worker = worker;
        _destination = destination;
        _permissions = permissions;
    }

    public async Task<byte[]> ProcessCobalt(IWopiWritableFile file, ClaimsPrincipal principal, byte[] newContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(newContent);
        var framing = WopiCobaltFraming.Detect(newContent);
        if (framing == WopiCobaltFraming.Kind.Unknown)
            throw new NotSupportedException("Unknown COBALT framing. SOAP and raw MS-FSSHTTPB are supported.");
        // Preflight client identity before importing or binding any document.
        var id = ResourceId(file.Identifier);
        CellStorageRequest? soap = null;
        if (framing == WopiCobaltFraming.Kind.Soap)
        {
            soap = CellStorageRequestParser.Parse(Encoding.UTF8.GetString(newContent));
            ValidateScope(soap, file, id);
        }
        var actor = await ActorAsync(file, principal, cancellationToken).ConfigureAwait(false);
        var state = await ImportAsync(file, actor, cancellationToken).ConfigureAwait(false);
        actor = actor with { AccessLimit = new(id, actor.AccessLimit!.Access) };
        byte[] response;
        if (soap is not null)
        {
            var urls = soap.Requests.Select(r => r.Url).ToArray();
            foreach (var request in soap.Requests)
            {
                request.Url = state.Path;
                request.UseResourceId = true;
                request.ResourceId = id.ToString("D");
            }
            var execution = await _requests.ExecuteAsync(soap, _options.WebOrigin, actor, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < Math.Min(urls.Length, execution.Response.Responses.Count); i++)
                execution.Response.Responses[i].Url = urls[i];
            response = Encoding.UTF8.GetBytes(execution.Response.ToSoapEnvelope());
        }
        else
        {
            var request = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(newContent));
            var execution = await _documents.ExecuteAsync(id, DocumentPartitionKind.FileContents, request,
                new Dictionary<string, string>(), actor, cancellationToken).ConfigureAwait(false);
            if (execution.LockError == "FileUnauthorizedAccess")
                throw new UnauthorizedAccessException();
            if (execution.LockError is not null)
                throw new WopiResourceLockedException(file.Identifier, execution.State.Coordination.HostLock?.Token ?? string.Empty);
            response = execution.Response.ToByteArray(_options.SerializationProfile);
        }
        // A pending delivery is durable work, not a request-completion callback. The packaged
        // worker also discovers it after restart; a failed immediate attempt leaves it queued.
        await _publisher.PublishNextAsync(id, cancellationToken).ConfigureAwait(false);
        _worker.Wake();
        return response;
    }

    public async Task<WopiEditingLockResult> ProcessLockAsync(IWopiFile file, ClaimsPrincipal principal,
        WopiEditingLockOperation operation, string? token, string? replacementToken = null,
        CancellationToken cancellationToken = default)
    {
        var actor = await ActorAsync(file, principal, cancellationToken).ConfigureAwait(false);
        if (!actor.AccessLimit!.Access.HasFlag(DocumentAccess.Write)) throw new UnauthorizedAccessException();
        var state = await ImportAsync(file, actor, cancellationToken).ConfigureAwait(false);
        if (operation == WopiEditingLockOperation.Get)
            return await ReadLockAsync(state.ResourceId, actor, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("An explicit lock token is required.", nameof(token));
        var mapped = operation switch
        {
            WopiEditingLockOperation.Acquire => HostLockOperation.Acquire,
            WopiEditingLockOperation.Refresh => HostLockOperation.Refresh,
            WopiEditingLockOperation.Release => HostLockOperation.Release,
            WopiEditingLockOperation.Replace => HostLockOperation.Replace,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        var result = await new SharedDocumentLocks(_documents).ApplyAsync(state.ResourceId, mapped,
            token, actor, replacementToken, cancellationToken).ConfigureAwait(false);
        if (result.Success) return new(true, result.CurrentLock);
        // Never expose an FSSHTTP schema/exclusive token as a WOPI lock identifier.
        var observed = await ReadLockAsync(state.ResourceId, actor, cancellationToken).ConfigureAwait(false);
        return new(false, observed.CurrentLock);
    }

    private ValueTask<WopiEditingLockResult> ReadLockAsync(Guid id, CellBridgeActor actor, CancellationToken ct) =>
        _documents.Provider.State.TransitionAsync(id, (state, now) =>
        {
            if (!_documents.Access(actor, state).HasFlag(DocumentAccess.Write)) throw new UnauthorizedAccessException();
            var coordination = state.Coordination;
            var foreign = coordination.Exclusive is { } exclusive && exclusive.ExpiresUtc > now ||
                coordination.SchemaOwners.Any(l => l.ExpiresUtc > now);
            var active = coordination.HostLock is { } host && host.ExpiresUtc > now ? host.Token : null;
            return new StateTransition<WopiEditingLockResult>(null, new(!foreign, active));
        }, ct);

    private async Task<CellBridgeActor> ActorAsync(IWopiFile file, ClaimsPrincipal principal, CancellationToken ct)
    {
        var identity = principal.Identities.FirstOrDefault(i => i.IsAuthenticated &&
            !string.IsNullOrWhiteSpace(i.FindFirst(ClaimTypes.NameIdentifier)?.Value));
        if (identity is null) throw new UnauthorizedAccessException("A stable authenticated WOPI subject is required.");
        var subject = "wopi:" + identity.FindFirst(ClaimTypes.NameIdentifier)!.Value;
        var name = identity.FindFirst(ClaimTypes.Name)?.Value ?? subject;
        var permission = await _permissions.GetFilePermissionsAsync(principal, file, ct).ConfigureAwait(false);
        var access = DocumentAccess.Read;
        if (permission.HasFlag(WopiFilePermissions.UserCanWrite)) access |= DocumentAccess.Write;
        return new(new(subject, name, name)) { AccessLimit = new(ResourceId(file.Identifier), access) };
    }

    private async Task<DocumentState> ImportAsync(IWopiFile file, CellBridgeActor actor, CancellationToken ct)
    {
        var id = ResourceId(file.Identifier);
        var provider = _documents.Provider;
        var current = await provider.State.FindByResourceIdAsync(id, ct).ConfigureAwait(false);
        if (current?.Publication is { } bound)
        {
            if (bound.Destination != file.Identifier) throw new InvalidOperationException("Destination binding mismatch.");
            if (!_documents.Access(actor, current).HasFlag(DocumentAccess.Read)) throw new UnauthorizedAccessException();
            return current;
        }
        var baseline = await _destination.ReadAsync(file.Identifier, ct).ConfigureAwait(false);
        var source = baseline.Content;
        await using var disposeSource = source.ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseline.Revision) || baseline.Length < 0 ||
            baseline.Length > provider.Limits.MaxDocumentBytes || baseline.Length > int.MaxValue)
            throw new InvalidDataException("Invalid or oversized destination baseline.");
        using var buffer = new MemoryStream();
        var block = new byte[65536];
        int read;
        while ((read = await source.ReadAsync(block, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > baseline.Length) throw new InvalidDataException("Destination baseline length mismatch.");
            await buffer.WriteAsync(block.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        var bytes = buffer.ToArray();
        if (bytes.LongLength != baseline.Length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != baseline.Sha256)
            throw new InvalidDataException("Destination baseline verification failed.");
        var importer = new CellBridgeActor(new("wopi:host", "wopi-host", "WOPI host"), CanCreate: true);
        current ??= await _documents.ImportAsync(id, DocumentPath(file), bytes, actor.Identity, importer, ct).ConfigureAwait(false);
        current ??= await provider.State.FindByResourceIdAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Document import did not create the selected resource.");
        // Another importer may have won. Bind only an exact verified byte baseline.
        if (current.Content.Length != baseline.Length || current.Content.Sha256 != baseline.Sha256)
            throw new InvalidDataException("The imported revision differs from the destination baseline.");
        if (current.Publication is null)
            await _publisher.BindAsync(id, current.LifecycleGeneration, current.StateVersion, Guid.NewGuid(),
                file.Identifier, baseline.Revision, ct).ConfigureAwait(false);
        current = await provider.State.FindByResourceIdAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Document disappeared during binding.");
        if (current.Publication is null || current.Publication.Destination != file.Identifier)
            throw new InvalidOperationException("The destination could not be bound atomically.");
        if (!_documents.Access(actor, current).HasFlag(DocumentAccess.Read)) throw new UnauthorizedAccessException();
        return current;
    }

    private Guid ResourceId(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(_options.ResourceNamespace.ToString("D") + ":" + identifier));
        return new Guid(hash.AsSpan(0, 16));
    }

    private string DocumentPath(IWopiFile file) => "/wopi/" + ResourceId(file.Identifier).ToString("N") +
        (string.IsNullOrEmpty(file.Extension) ? "" : "." + file.Extension);

    private void ValidateScope(CellStorageRequest request, IWopiFile file, Guid id)
    {
        foreach (var item in request.Requests)
        {
            if (item.ResourceId is not null && (!Guid.TryParse(item.ResourceId, out var supplied) || supplied != id))
                throw new UnauthorizedAccessException("The SOAP resource does not match the WOPI route.");
            if (item.UseResourceId)
            {
                if (item.ResourceId is null) throw new InvalidDataException("UseResourceID requires a resource ID.");
            }
            else
            {
                if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var url) ||
                    url.GetLeftPart(UriPartial.Authority) != _options.WebOrigin.TrimEnd('/') ||
                    url.AbsolutePath != "/wopi/files/" + Uri.EscapeDataString(file.Identifier))
                    throw new UnauthorizedAccessException("The SOAP URL does not match the WOPI route.");
            }
            if (item.SubRequests.Any(s => s.Type == SubRequestType.FileOperation))
                throw new NotSupportedException("Coordinated file renaming is not implemented.");
        }
    }
}

/// <summary>Settings for the opt-in coordinated adapter.</summary>
public sealed class CoordinatedCellBridgeOptions
{
    public Guid ResourceNamespace { get; set; }
    public string WebOrigin { get; set; } = "";
    public FsshttpbSerializationProfile SerializationProfile { get; set; } = FsshttpbSerializationProfile.SharePoint13_11;
    /// <summary>Allows disposable single-process experiments. Recovery is unavailable in this mode.</summary>
    public bool AllowVolatileProtocolState { get; set; }

    internal void Validate()
    {
        if (ResourceNamespace == Guid.Empty || !Uri.TryCreate(WebOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") || origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            throw new InvalidOperationException("Set a stable resource namespace GUID and a public HTTP(S) origin.");
    }
}
