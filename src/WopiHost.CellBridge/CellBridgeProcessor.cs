using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WopiHost.Abstractions;

namespace WopiHost.CellBridge;

/// <summary>
/// MS-FSSHTTP processor backed by the open-source cellbridge engine instead of the proprietary
/// <c>Microsoft.CobaltCore</c>.
/// </summary>
/// <remarks>
/// <para>
/// cellbridge keeps protocol state (storage index, data-element graph, knowledge, editor sessions,
/// locks) in a <see cref="StorageProvider"/>. This processor uses the in-memory stores, the same
/// posture as <c>WopiHost.Cobalt</c>'s in-memory <c>LocalHostBlobStore</c>: each WOPI file is imported
/// into cellbridge on first use and the materialized document is written back through
/// <see cref="IWopiWritableFile.OpenWriteAsync"/> for accepted saves, including retries after a failed write.
/// </para>
/// <para>
/// The WOPI <c>COBALT</c> override hands over the request body without its Content-Type. cellbridge
/// speaks SOAP (what desktop Office sends to SharePoint's <c>cellstorage.svc</c>) and raw MS-FSSHTTPB
/// (the binary cell request carried inside a SOAP <c>Cell</c> subrequest). Which framing Office Online
/// Server uses over WOPI has not been captured yet, so both are accepted and anything else fails
/// loudly instead of answering garbage.
/// </para>
/// </remarks>
public sealed partial class CellBridgeProcessor : ICobaltProcessor, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> s_noAttributes = new Dictionary<string, string>(StringComparer.Ordinal);

    // Imports run on behalf of the host, not the editing user: ownership is recorded for the user,
    // creation permission for the host.
    private static readonly CellBridgeActor s_importer = new(new SubjectIdentity("wopi:host", "wopi-host", "WOPI host"), CanCreate: true);

    private readonly ILogger<CellBridgeProcessor> _logger;
    private readonly CellBridgeProcessorOptions _options;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly StorageProvider _storage;
    private readonly CellBridgeDocumentService _documents;
    private readonly ConcurrentDictionary<string, Lazy<Task<Guid>>> _resources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _writeLocks = new();
    private readonly ConcurrentDictionary<Guid, uint> _writtenVersions = new();
    private readonly Guid _libraryId = Guid.NewGuid();
    private int _disposed;

    public CellBridgeProcessor(ILogger<CellBridgeProcessor> logger, IOptions<CellBridgeProcessorOptions> options, IHttpContextAccessor? httpContextAccessor = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _httpContextAccessor = httpContextAccessor;
        _storage = CreateVolatileStorage();
        _documents = new CellBridgeDocumentService(_storage, new WopiCellBridgeAccessEvaluator());
    }

    /// <inheritdoc/>
    public async Task<byte[]> ProcessCobalt(IWopiWritableFile file, ClaimsPrincipal principal, byte[] newContent, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(newContent);

        var actor = ActorFor(principal);
        var framing = WopiCobaltFraming.Detect(newContent);
        var soap = framing == WopiCobaltFraming.Kind.Soap
            ? CellStorageRequestParser.Parse(Encoding.UTF8.GetString(newContent)) : null;
        if (soap is not null && CellBridgeSoapAdmission.RequiresRejection(soap))
        {
            // Let CellBridge produce its admission rejection before importing or executing any operation.
            var rejected = await new CellBridgeRequestProcessor(_documents).ExecuteAsync(soap,
                PublicOrigin(), actor, cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetBytes(rejected.Response.ToSoapEnvelope());
        }
        var resourceId = await GetOrImportAsync(file, actor, cancellationToken).ConfigureAwait(false);
        actor = actor with { AccessLimit = new DocumentAccessLimit(resourceId, DocumentAccess.Read | DocumentAccess.Write) };

        switch (framing)
        {
            case WopiCobaltFraming.Kind.Fsshttpb:
                return await ExecuteBinaryAsync(file, resourceId, actor, newContent, cancellationToken).ConfigureAwait(false);
            case WopiCobaltFraming.Kind.Soap:
                return await ExecuteSoapAsync(file, resourceId, actor, soap!, cancellationToken).ConfigureAwait(false);
            default:
                LogUnknownFraming(_logger, file.Identifier, Convert.ToHexString(newContent.AsSpan(0, Math.Min(newContent.Length, 16))));
                throw new NotSupportedException(
                    "The COBALT request body is neither a raw MS-FSSHTTPB cell request nor a SOAP cell storage request. " +
                    "Capture the body and see src/WopiHost.CellBridge/README.md.");
        }
    }

    private async Task<byte[]> ExecuteBinaryAsync(IWopiWritableFile file, Guid resourceId, CellBridgeActor actor, byte[] body, CancellationToken cancellationToken)
    {
        var request = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(body));
        var kind = DocumentPartitionKind.FileContents;
        var target = request.SubRequests.Select(s => s.TargetPartitionId).FirstOrDefault(t => t.HasValue);
        if (target is { } partitionId && !CellPartitionSelector.TryResolve(partitionId, out kind))
        {
            throw new NotSupportedException($"Unknown MS-FSSHTTPB target partition {partitionId}.");
        }

        var execution = await ExecuteCellAsync(file, resourceId, kind, request, s_noAttributes, actor, cancellationToken).ConfigureAwait(false);
        return execution.Response.ToByteArray(_options.SerializationProfile);
    }

    private async Task<byte[]> ExecuteSoapAsync(IWopiWritableFile file, Guid resourceId, CellBridgeActor actor, CellStorageRequest request, CancellationToken cancellationToken)
    {
        var response = new CellStorageResponse
        {
            Version = request.Version,
            // SharePoint answers a 2.x request with the highest minor revision it supports.
            MinorVersion = request.Version == 2 ? 3u : request.MinorVersion,
            SoapNamespace = request.SoapNamespace,
            WebUrl = PublicOrigin(),
        };

        foreach (var fileRequest in request.Requests)
        {
            var fileResponse = new FssHttpResponse
            {
                Url = fileRequest.Url,
                RequestToken = fileRequest.RequestToken,
                IntervalOverride = 0,
                ResourceId = resourceId,
            };

            foreach (var subRequest in fileRequest.SubRequests)
            {
                var subResponse = new FssHttpSubResponse
                {
                    Type = subRequest.Type,
                    SubRequestToken = subRequest.SubRequestToken,
                    ErrorCode = "Success",
                    HResult = "0",
                };

                if (FssHttpDependencies.Error(subRequest, fileResponse.SubResponses, fileRequest.SubRequests) is { } dependencyError)
                {
                    subResponse.ErrorCode = dependencyError;
                    subResponse.HResult = "2147500037";
                    subResponse.EmitEmptySubResponseData = true;
                }
                else
                {
                    await ApplySubRequestAsync(file, resourceId, actor, request, fileRequest.Url, subRequest, subResponse, cancellationToken).ConfigureAwait(false);
                }

                fileResponse.SubResponses.Add(subResponse);
            }

            response.Responses.Add(fileResponse);
        }

        return Encoding.UTF8.GetBytes(response.ToSoapEnvelope());
    }

    private async Task ApplySubRequestAsync(IWopiWritableFile file, Guid resourceId, CellBridgeActor actor, CellStorageRequest envelope, string clientUrl, FssHttpSubRequest subRequest, FssHttpSubResponse subResponse, CancellationToken cancellationToken)
    {
        if (subRequest.Type == SubRequestType.GetVersions)
        {
            // The WOPI adapter has no CellBridge history download routes. Report the current
            // file at the client's URL without advertising version restoration or downloads.
            var state = await _documents.GetDocumentMetadataAsync(resourceId, actor, cancellationToken).ConfigureAwait(false);
            subResponse.SubResponseXml = new XElement("GetVersionsResponse",
                new XElement("GetVersionsResult", new XElement("results",
                    new XElement("list", new XAttribute("id", _libraryId.ToString("B"))),
                    new XElement("versioning", new XAttribute("enabled", "0")),
                    new XElement("settings", new XAttribute("url", "")),
                    new XElement("result",
                        new XAttribute("version", "@" + state.ContentVersion.ToString(CultureInfo.InvariantCulture) + ".0"),
                        new XAttribute("url", clientUrl),
                        new XAttribute("created", state.ModifiedUtc.ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture)),
                        new XAttribute("createdRaw", state.ModifiedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
                        new XAttribute("createdBy", state.Security.ModifiedBy?.Login ?? actor.Identity.Login),
                        new XAttribute("createdByName", state.Security.ModifiedBy?.DisplayName ?? actor.Identity.DisplayName),
                        new XAttribute("size", state.Package.Length), new XAttribute("comments", "")))))
                .ToString(SaveOptions.DisableFormatting);
            return;
        }

        if (subRequest.Type is not (SubRequestType.Cell or SubRequestType.WhoAmI or SubRequestType.ServerTime or
            SubRequestType.SchemaLock or SubRequestType.ExclusiveLock or SubRequestType.LockStatus or
            SubRequestType.AmIAlone or SubRequestType.Coauth or SubRequestType.EditorsTable or SubRequestType.GetDocMetaInfo))
        {
            subResponse.ErrorCode = "NotSupported";
            return;
        }

        // Preserve the adapter's InvalidArgument response for malformed embedded cell data.
        if (subRequest.Type == SubRequestType.Cell && DecodeCellPayload(subRequest) is null)
        {
            subResponse.ErrorCode = "InvalidArgument";
            return;
        }

        var request = new CellStorageRequest
        {
            Version = envelope.Version,
            MinorVersion = envelope.MinorVersion,
            SoapNamespace = envelope.SoapNamespace,
        };
        var fileRequest = new FssHttpRequest { Url = clientUrl, UseResourceId = true, ResourceId = resourceId.ToString("D") };
        // The outer loop evaluated dependencies against all earlier operations. Delegate the
        // admitted operation without asking CellBridge to resolve a partial dependency list.
        var operation = new FssHttpSubRequest
        {
            Type = subRequest.Type,
            SubRequestToken = subRequest.SubRequestToken,
            SubRequestDataXml = subRequest.SubRequestDataXml,
            SubRequestDataBinaryMemory = subRequest.SubRequestDataBinaryMemory,
        };
        foreach (var attribute in subRequest.SubRequestDataAttributes)
            operation.SubRequestDataAttributes[attribute.Key] = attribute.Value;
        fileRequest.SubRequests.Add(operation);
        request.Requests.Add(fileRequest);
        var execution = await WriteBackAsync(file, resourceId, async () =>
        {
            var result = await new CellBridgeRequestProcessor(_documents).ExecuteAsync(request,
                PublicOrigin(), actor, cancellationToken).ConfigureAwait(false);
            return (result, result.AcceptedSaves);
        }, cancellationToken).ConfigureAwait(false);
        var response = execution.Response.Responses.Single().SubResponses.Single();
        subResponse.ErrorCode = response.ErrorCode;
        subResponse.HResult = response.HResult;
        subResponse.ErrorMessage = response.ErrorMessage;
        subResponse.EmitEmptySubResponseData = response.EmitEmptySubResponseData;
        subResponse.SubResponseDataXml = response.SubResponseDataXml;
        subResponse.SubResponseXml = response.SubResponseXml;
        subResponse.SubResponseDataBase64 = response.SubResponseDataBase64;
        if (subRequest.Type == SubRequestType.Cell && response.SubResponseDataBase64 is { } cellResponse)
        {
            // The reusable SOAP processor emits SharePoint framing. Keep this adapter's
            // configured framing consistent with raw Cell responses.
            subResponse.SubResponseDataBase64 = FsshttpbResponse.Deserialize(new BinaryReaderEx(cellResponse))
                .ToByteArray(_options.SerializationProfile);
        }
        foreach (var attribute in response.SubResponseDataAttributes)
            subResponse.SubResponseDataAttributes[attribute.Key] = attribute.Value;
    }

    // SOAP carries the binary cell request either as an MTOM part (already resolved into
    // SubRequestDataBinaryMemory by the parser) or as base64 text inside SubRequestData.
    private static FsshttpbCellRequest? DecodeCellPayload(FssHttpSubRequest subRequest)
    {
        try
        {
            if (subRequest.SubRequestDataBinaryMemory is { } binary)
            {
                return FsshttpbCellRequest.Deserialize(new BinaryReaderEx(binary));
            }

            if (subRequest.SubRequestDataXml is { } xml)
            {
                var text = XDocument.Parse(xml).Root?.Value;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return FsshttpbCellRequest.Deserialize(new BinaryReaderEx(Convert.FromBase64String(text.Trim())));
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException or ArgumentException or System.Xml.XmlException)
        {
            return null;
        }

        return null;
    }

    private string PublicOrigin()
    {
        if (!string.IsNullOrEmpty(_options.WebOrigin)) return _options.WebOrigin;
        var request = _httpContextAccessor?.HttpContext?.Request
            ?? throw new InvalidOperationException($"SOAP framing requires {CellBridgeProcessorOptions.SectionName}:{nameof(CellBridgeProcessorOptions.WebOrigin)} or the current HTTP request origin.");
        return request.Scheme + "://" + request.Host.ToUriComponent();
    }

    private static StorageProvider CreateVolatileStorage()
    {
        var content = new InMemoryContentStore();
        return new(new InMemoryDocumentRepository(content), content);
    }

    private async Task<CellExecution> ExecuteCellAsync(IWopiWritableFile file, Guid resourceId, DocumentPartitionKind kind, FsshttpbCellRequest request, IReadOnlyDictionary<string, string> attributes, CellBridgeActor actor, CancellationToken cancellationToken)
    {
        var execution = await WriteBackAsync(file, resourceId, async () =>
        {
            var result = await _documents.ExecuteAsync(resourceId, kind, request, attributes, actor, cancellationToken).ConfigureAwait(false);
            return (result, result.AcceptedSaves);
        }, cancellationToken).ConfigureAwait(false);
        if (execution.LockError is { } lockError) LogLockError(_logger, file.Identifier, lockError);
        return execution;
    }

    private async Task<T> WriteBackAsync<T>(IWopiWritableFile file, Guid resourceId, Func<Task<(T Result, ImmutableArray<AcceptedSave> Saves)>> execute, CancellationToken cancellationToken)
    {
        (T Result, ImmutableArray<AcceptedSave> Saves) execution;
        try
        {
            execution = await execute().ConfigureAwait(false);
        }
        catch (AcceptedSaveException ex)
        {
            await MirrorContentAsync(file, resourceId, ex.AcceptedSaves, cancellationToken).ConfigureAwait(false);
            throw;
        }
        await MirrorContentAsync(file, resourceId, execution.Saves, cancellationToken).ConfigureAwait(false);
        return execution.Result;
    }

    private async Task MirrorContentAsync(IWopiWritableFile file, Guid resourceId, ImmutableArray<AcceptedSave> saves, CancellationToken cancellationToken)
    {
        if (saves.IsEmpty) return;
        var gate = _writeLocks.GetOrAdd(resourceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Replay receipts can refer to an earlier operation but carry the current package.
            // Resolve the newest accepted content under the write gate so delayed completions
            // and retries after failed writes cannot replace a newer package with an older one.
            var state = await _storage.Documents.FindAsync(new DocumentIdentity.Resource(resourceId), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The imported WOPI document is missing.");
            if (_writtenVersions.TryGetValue(resourceId, out var written) && written >= state.ContentVersion) return;
            var source = await _storage.Packages.OpenReadAsync(state.Package, cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var target = await file.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
                await using (target.ConfigureAwait(false))
                {
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }
            }
            // Disposing the target commits some WOPI providers. A failed commit must remain retriable.
            _writtenVersions[resourceId] = state.ContentVersion;
            LogContentFlushed(_logger, file.Identifier, state.ContentVersion);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Guid> GetOrImportAsync(IWopiWritableFile file, CellBridgeActor owner, CancellationToken cancellationToken)
    {
        var lazy = _resources.GetOrAdd(
            file.Identifier,
            _ => new Lazy<Task<Guid>>(() => ImportAsync(file, owner, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A faulted Lazy<Task<>> would otherwise be cached forever; remove exactly this entry so
            // the next call retries.
            _resources.TryRemove(new KeyValuePair<string, Lazy<Task<Guid>>>(file.Identifier, lazy));
            LogImportFailed(_logger, ex, file.Identifier);
            throw;
        }
    }

    private async Task<Guid> ImportAsync(IWopiWritableFile file, CellBridgeActor owner, CancellationToken cancellationToken)
    {
        byte[] bytes = [];
        if (file.Exists)
        {
            var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                bytes = buffer.ToArray();
            }
        }

        var path = DocumentPath(file);
        var state = await _documents.ImportAsync(path, bytes, owner.Identity, s_importer, cancellationToken).ConfigureAwait(false)
            ?? await _storage.Documents.FindAsync(new DocumentIdentity.Path(StorageIds.PathKey(DocumentStore.NormalizeUrl(path))), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"cellbridge did not register a document for WOPI file '{file.Identifier}'.");

        LogDocumentImported(_logger, file.Identifier, state.ResourceId);
        return state.ResourceId;
    }

    // cellbridge validates Office packages by the extension in the document path, so the WOPI
    // file's extension has to survive into cellbridge's URL space.
    private static string DocumentPath(IWopiFile file)
    {
        var name = "/wopi/" + Uri.EscapeDataString(file.Identifier);
        return string.IsNullOrEmpty(file.Extension) ? name : name + "." + file.Extension;
    }

    private static CellBridgeActor ActorFor(ClaimsPrincipal? principal)
    {
        var id = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var name = principal?.FindFirst(ClaimTypes.Name)?.Value;
        var subject = string.IsNullOrEmpty(id) ? "wopi:anonymous" : "wopi:" + id;
        var login = string.IsNullOrEmpty(name) ? subject : name;
        return new CellBridgeActor(new SubjectIdentity(subject, login, login));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (var gate in _writeLocks.Values)
        {
            gate.Dispose();
        }

        _writeLocks.Clear();
        _writtenVersions.Clear();
        _resources.Clear();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, typeof(CellBridgeProcessor));
    }
}
