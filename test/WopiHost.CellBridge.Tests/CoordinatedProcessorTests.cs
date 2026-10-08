using System.Security.Claims;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using FakeItEasy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WopiHost.Abstractions;
using Xunit;

namespace WopiHost.CellBridge.Tests;

public sealed class CoordinatedProcessorTests
{
    [Fact]
    public async Task SharedSoapProcessorAnswersPreviouslyUnsupportedMetadata()
    {
        using var host = new Host();
        var response = await host.Processor.ProcessCobalt(host.File, Principal(), Soap("<SubRequest Type=\"GetDocMetaInfo\" SubRequestToken=\"1\" />"));
        Assert.Equal("Success", Error(response));
        Assert.Equal(0, host.Destination.Writes);
        A.CallTo(() => host.File.OpenWriteAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task StaleUnlockAndDifferentSubjectCannotRemoveReplacementLock()
    {
        using var host = new Host();
        Assert.True((await host.Lock(WopiEditingLockOperation.Acquire, "old")).Success);
        Assert.True((await host.Lock(WopiEditingLockOperation.Replace, "old", "new")).Success);
        Assert.False((await host.Lock(WopiEditingLockOperation.Release, "old")).Success);
        Assert.False((await host.Processor.ProcessLockAsync(host.File, Principal("other"), WopiEditingLockOperation.Release, "new")).Success);
        Assert.Equal("new", (await host.Lock(WopiEditingLockOperation.Get)).CurrentLock);
        Assert.True((await host.Lock(WopiEditingLockOperation.Release, "new")).Success);
    }

    [Fact]
    public async Task WopiAndFsshttpAcquisitionHaveOneWinnerAcrossAdapterInstances()
    {
        using var host = new Host();
        await host.Query();
        var other = ActivatorUtilities.CreateInstance<CoordinatedCellBridgeProcessor>(host.Services);
        var wopi = other.ProcessLockAsync(host.File, Principal(), WopiEditingLockOperation.Acquire, "wopi");
        var fss = host.Processor.ProcessCobalt(host.File, Principal(), Exclusive());
        await Task.WhenAll(wopi, fss);
        Assert.NotEqual((await wopi).Success, Error(await fss) == "Success");
        var state = await host.State();
        Assert.False(state.Coordination.HostLock is not null && state.Coordination.Exclusive is not null);
    }

    [Fact]
    public async Task FsshttpLockBlocksWopiWithoutExposingItsToken()
    {
        using var host = new Host();
        Assert.Equal("Success", Error(await host.Processor.ProcessCobalt(host.File, Principal(), Exclusive())));
        var refused = await host.Lock(WopiEditingLockOperation.Acquire, "wopi");
        Assert.False(refused.Success);
        Assert.Null(refused.CurrentLock);
    }

    [Fact]
    public async Task WopiLockBlocksFsshttpSaveBeforeAnyExternalWrite()
    {
        using var host = new Host();
        var query = await host.Query();
        Assert.True((await host.Lock(WopiEditingLockOperation.Acquire, "wopi")).Success);
        var body = await host.PutAsync(query, "changed"u8.ToArray());
        await Assert.ThrowsAsync<WopiResourceLockedException>(() => host.Processor.ProcessCobalt(host.File, Principal(), body));
        Assert.Equal(0, host.Destination.Writes);
    }

    [Fact]
    public async Task RevocationClearsTheLockAndDeniesFurtherMutations()
    {
        using var host = new Host();
        Assert.True((await host.Lock(WopiEditingLockOperation.Acquire, "held")).Success);
        var state = await host.State();
        Assert.True(await host.Services.GetRequiredService<CellBridgeDocumentService>().UpdateAuthorizationAsync(
            state.ResourceId, state.Security.AuthorizationPolicy, new("test-policy", 1, 2)));
        Assert.Null((await host.State()).Coordination.HostLock);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Lock(WopiEditingLockOperation.Refresh, "held"));
    }

    [Fact]
    public async Task RefreshFencesAWritePreparedUnderThePreviousLockEpoch()
    {
        using var host = new Host();
        Assert.True((await host.Lock(WopiEditingLockOperation.Acquire, "held")).Success);
        var before = await host.State();
        var token = new HostWriteToken(before.ResourceId, before.LifecycleGeneration,
            before.Coordination.Generation, "held", "wopi:owner");
        Assert.True((await host.Lock(WopiEditingLockOperation.Refresh, "held")).Success);
        var after = await host.State();
        var callbackCalled = false;
        var actor = new CellBridgeActor(new("wopi:owner", "owner", "owner"));
        var committed = await new SharedDocumentLocks(host.Services.GetRequiredService<CellBridgeDocumentService>())
            .TryCommitAsync(token, after.StateVersion, actor, (state, _) => { callbackCalled = true; return state; }, Guid.NewGuid());
        Assert.False(committed);
        Assert.False(callbackCalled);
    }

    [Fact]
    public async Task ExpiredTokenCannotRefreshOrReleaseANewOwnersLock()
    {
        using var host = new Host();
        Assert.True((await host.Lock(WopiEditingLockOperation.Acquire, "expired")).Success);
        var state = await host.State();
        await host.Storage.State.TransitionAsync(state.ResourceId, (current, now) => new StateTransition<bool>(current with
        { Coordination = current.Coordination with { HostLock = current.Coordination.HostLock! with { ExpiresUtc = now.AddSeconds(-1) } } }, true));
        Assert.Null((await host.Lock(WopiEditingLockOperation.Get)).CurrentLock);
        Assert.False((await host.Lock(WopiEditingLockOperation.Refresh, "expired")).Success);
        Assert.True((await host.Processor.ProcessLockAsync(host.File, Principal("other"), WopiEditingLockOperation.Acquire, "new")).Success);
        Assert.False((await host.Lock(WopiEditingLockOperation.Release, "expired")).Success);
        Assert.Equal("new", (await host.Lock(WopiEditingLockOperation.Get)).CurrentLock);
    }

    [Theory]
    [InlineData("https://wopi.example.test/wopi/files/other", "")]
    [InlineData("https://wopi.example.test/wopi/files/file-1", "UseResourceID=\"true\" ResourceID=\"00000000-0000-0000-0000-000000000001\"")]
    public async Task CrossFileRequestsNeverImportOrReachAnotherResource(string url, string attributes)
    {
        using var host = new Host();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Processor.ProcessCobalt(host.File, Principal(),
            Soap("<SubRequest Type=\"WhoAmI\" SubRequestToken=\"1\" />", url, attributes)));
        Assert.Empty(await host.Storage.State.ListAsync(0, 10));
    }

    [Fact]
    public async Task UnauthenticatedSubjectsAreRejected()
    {
        using var host = new Host();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Processor.ProcessCobalt(host.File, new ClaimsPrincipal(), QueryBytes()));
        Assert.Empty(await host.Storage.State.ListAsync(0, 10));
    }

    [Fact]
    public async Task ReadOnlyPermissionCeilingIsReflectedInQueryAccess()
    {
        using var host = new Host(writePermission: false);
        var body = new FsshttpbCellRequest();
        body.SubRequests.Add(new(RequestTypes.QueryAccess) { RequestId = 1, Data = new QueryAccessSubRequestData() });
        var response = FsshttpbResponse.Deserialize(new BinaryReaderEx(await host.Processor.ProcessCobalt(host.File, Principal(), body.ToByteArray())));
        var access = Assert.IsType<QueryAccessSubResponseData>(Assert.Single(response.SubResponses).Data);
        Assert.Null(access.ReadAccessError);
        Assert.NotNull(access.WriteAccessError);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Lock(WopiEditingLockOperation.Acquire, "lock"));
    }

    [Fact]
    public async Task LostAcknowledgementIsRecoveredByNewPublisherWithoutRepeatingTheWrite()
    {
        using var host = new Host();
        var query = await host.Query();
        host.Destination.LoseNextAcknowledgement = true;
        await host.Processor.ProcessCobalt(host.File, Principal(), await host.PutAsync(query, "saved"u8.ToArray()));
        Assert.Equal(1, host.Destination.Writes);
        var state = await host.State();
        var pending = Assert.Single(state.Publication!.Pending);
        var reconstructed = new ExternalRevisionPublisher(host.Storage, new WopiRevisionDestination(host.Destination));
        var attempts = await Task.WhenAll(reconstructed.PublishNextAsync(state.ResourceId).AsTask(),
            reconstructed.PublishNextAsync(state.ResourceId).AsTask());
        Assert.Contains(PublicationAttempt.Delivered, attempts);
        Assert.Empty((await host.State()).Publication!.Pending);
        Assert.Equal(1, host.Destination.Writes);
        Assert.Equal("saved", ReadText(host.Destination.Content));
        await using var bytes = await host.Storage.Content.OpenReadAsync(pending.Content);
        var changed = new ExternalDeliveryRequest(state.Publication.BindingId, "file-1", "changed-baseline", pending);
        await Assert.ThrowsAsync<InvalidDataException>(() => new WopiRevisionDestination(host.Destination).CompareExchangeAsync(changed, bytes).AsTask());
    }

    [Fact]
    public async Task RemoteChangesArePreservedAndUndeliveredBytesRemainQueued()
    {
        using var host = new Host();
        var query = await host.Query();
        host.Destination.RemoteWrite(MinimalDocx("remote"));
        await host.Processor.ProcessCobalt(host.File, Principal(), await host.PutAsync(query, "local"u8.ToArray()));
        Assert.Equal("remote", ReadText(host.Destination.Content));
        var pending = (await host.State()).Publication!;
        Assert.Single(pending.Pending);
        Assert.Equal("DestinationConflict", pending.BlockedReason);
    }

    [Fact]
    public void ProductionRegistrationRejectsVolatileProtocolState()
    {
        using var host = new Host(allowVolatile: false);
        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<ICobaltProcessor>());
    }

    [Fact]
    public void DurableMetadataWithVolatileContentIsRejected()
    {
        var state = A.Fake<IDocumentStateStore>();
        A.CallTo(() => state.Durable).Returns(true);
        var storage = new StorageProvider(state, new InMemoryContentStore());
        Assert.True(storage.State.Durable);
        Assert.False(storage.Capabilities.Durable);
        using var host = new Host(allowVolatile: false, storage: storage);
        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<ICobaltProcessor>());
    }

    private static ClaimsPrincipal Principal(string id = "owner") => new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id)], "test"));
    private static byte[] QueryBytes()
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1, Data = new QueryChangesSubRequestData() });
        return request.ToByteArray();
    }

    private static byte[] Put(FsshttpbResponse query, byte[] content, CellId cell)
    {
        var data = Assert.IsType<QueryChangesSubResponseData>(Assert.Single(query.SubResponses).Data);
        static ExGuid Fresh() => new(1, Guid.NewGuid());
        var identity = new StorageManifestBuilder.StableIdentity(Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), cell, Guid.NewGuid());
        var graph = FileContentPartitionBuilder.BuildQueryChangesResponse(1, MinimalDocx(Encoding.UTF8.GetString(content)), identity, data.CellKnowledgeTo + 1);
        var request = new FsshttpbCellRequest { DataElementPackage = graph.DataElementPackage };
        request.SubRequests.Add(new(RequestTypes.PutChanges) { RequestId = 2, Data = new PutChangesSubRequestData
        { StorageIndex = ((QueryChangesSubResponseData)graph.SubResponses.Single().Data!).StorageIndexExtendedGuid, ExpectedStorageIndex = data.StorageIndexExtendedGuid } });
        return request.ToByteArray();
    }

    private static byte[] MinimalDocx(string text)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open()))
                writer.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
                writer.Write($"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>");
        }
        return buffer.ToArray();
    }
    private static string ReadText(byte[] bytes)
    {
        using var buffer = new MemoryStream(bytes);
        using var archive = new ZipArchive(buffer);
        using var source = archive.GetEntry("word/document.xml")!.Open();
        return XDocument.Load(source).Descendants().Single(e => e.Name.LocalName == "t").Value;
    }

    private static byte[] Exclusive() => Soap($"<SubRequest Type=\"ExclusiveLock\" SubRequestToken=\"1\"><SubRequestData ExclusiveLockRequestType=\"GetLock\" ExclusiveLockID=\"{Guid.NewGuid():D}\" ClientID=\"{Guid.NewGuid():D}\" Timeout=\"3600\" /></SubRequest>");
    private static string? Error(byte[] response) => (string?)XDocument.Parse(Encoding.UTF8.GetString(response)).Descendants()
        .Single(e => e.Name.LocalName == "SubResponse").Attribute("ErrorCode");
    private static byte[] Soap(string subrequest, string url = "https://wopi.example.test/wopi/files/file-1", string attributes = "") => Encoding.UTF8.GetBytes($"""
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body>
          <ExecuteCellStorageRequest xmlns="http://schemas.microsoft.com/sharepoint/soap/">
            <RequestVersion Version="2" MinorVersion="2" />
            <RequestCollection CorrelationId="6B29FC40-CA47-1067-B31D-00DD010662DA"><Request Url="{url}" RequestToken="1" {attributes}>{subrequest}</Request></RequestCollection>
          </ExecuteCellStorageRequest>
        </soap:Body></soap:Envelope>
        """);

    private sealed class Host : IDisposable
    {
        public StorageProvider Storage { get; } = new(new InMemoryStateStore(), new InMemoryContentStore());
        public TestRevisionStore Destination { get; } = new();
        public IWopiWritableFile File { get; } = A.Fake<IWopiWritableFile>();
        public ServiceProvider Services { get; }
        public CoordinatedCellBridgeProcessor Processor => Services.GetRequiredService<CoordinatedCellBridgeProcessor>();
        public Host(bool writePermission = true, bool allowVolatile = true, StorageProvider? storage = null)
        {
            if (storage is not null) Storage = storage;
            A.CallTo(() => File.Identifier).Returns("file-1");
            A.CallTo(() => File.Extension).Returns("docx");
            A.CallTo(() => File.Exists).Returns(true);
            var permissions = A.Fake<IWopiPermissionProvider>();
            A.CallTo(() => permissions.GetFilePermissionsAsync(A<ClaimsPrincipal>._, A<IWopiFile>._, A<CancellationToken>._))
                .Returns(Task.FromResult(writePermission ? WopiFilePermissions.UserCanWrite : WopiFilePermissions.ReadOnly));
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Destination);
            services.AddSingleton(permissions);
            services.AddCellBridge<TestPolicy>(Storage, requireDurability: false);
            services.AddCoordinatedCellBridgeProcessor<TestRevisionStore>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Wopi:CellBridge:Coordinated:ResourceNamespace"] = "8a811ee1-797b-4e63-8595-dfa410e55819",
                ["Wopi:CellBridge:Coordinated:WebOrigin"] = "https://wopi.example.test",
                ["Wopi:CellBridge:Coordinated:SerializationProfile"] = "Current",
                ["Wopi:CellBridge:Coordinated:AllowVolatileProtocolState"] = allowVolatile.ToString()
            }).Build());
            Services = services.BuildServiceProvider();
        }
        public Task<WopiEditingLockResult> Lock(WopiEditingLockOperation operation, string? token = null, string? replacement = null) =>
            Processor.ProcessLockAsync(File, Principal(), operation, token, replacement);
        public async Task<FsshttpbResponse> Query() => FsshttpbResponse.Deserialize(new BinaryReaderEx(await Processor.ProcessCobalt(File, Principal(), QueryBytes())));
        public async Task<DocumentState> State() => (await Storage.State.FindByResourceIdAsync((await Storage.State.ListAsync(0, 1)).Single().ResourceId))!;
        public async Task<byte[]> PutAsync(FsshttpbResponse query, byte[] bytes)
        {
            var partition = (await State()).Partitions.Single(p => p.Kind == 0);
            return Put(query, bytes, new CellId(new ExGuid(partition.Identity.CellLong.Value, partition.Identity.CellLong.Guid),
                new ExGuid(partition.Identity.CellShort.Value, partition.Identity.CellShort.Guid)));
        }
        public void Dispose() => Services.Dispose();
    }

    private sealed class TestPolicy : ICellBridgeAuthorizationPolicy
    {
        public string PolicyDomain => "test-policy";
        public DocumentAuthorizationBinding BindNewDocument(Guid resourceId) => new(PolicyDomain, 1, 1);
        public ICellBridgeAuthorizationSnapshot? Resolve(DocumentState state) => new Snapshot(state.ResourceId, state.Security.AuthorizationPolicy!);
        private sealed record Snapshot(Guid ResourceId, DocumentAuthorizationBinding Binding) : ICellBridgeAuthorizationSnapshot
        {
            public DocumentAccess Evaluate(string subject) => Binding.Revision == 1 ? DocumentAccess.Read | DocumentAccess.Write : DocumentAccess.None;
        }
    }

    // Shared transactional state models a provider's durable bytes/token/receipt operation.
    // These are synthetic API tests, not qualification of a production storage backend.
    private sealed class TestRevisionStore : IWopiRevisionStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<(Guid, Guid), (WopiRevisionWrite Request, string Result)> _receipts = new();
        private long _version;
        public byte[] Content { get; private set; } = MinimalDocx("initial");
        public int Writes { get; private set; }
        public bool LoseNextAcknowledgement { get; set; }
        public Task<WopiRevisionRead> ReadAsync(string fileId, CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult(new WopiRevisionRead(new MemoryStream(Content.ToArray(), writable: false),
                "r" + _version, Content.LongLength, Convert.ToHexStringLower(SHA256.HashData(Content))));
        }
        public async Task<WopiRevisionWriteResult> CompareExchangeAsync(WopiRevisionWrite request, Stream content, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            Assert.Equal(request.Length, bytes.LongLength);
            Assert.Equal(request.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            lock (_gate)
            {
                if (_receipts.TryGetValue((request.BindingId, request.OperationId), out var receipt))
                {
                    if (receipt.Request != request) throw new InvalidDataException("Changed operation fingerprint.");
                    return new(WopiRevisionWriteStatus.Applied, receipt.Result);
                }
                if (request.ExpectedRevision != "r" + _version) return new(WopiRevisionWriteStatus.Conflict);
                Content = bytes;
                var next = "r" + ++_version;
                _receipts.Add((request.BindingId, request.OperationId), (request, next));
                Writes++;
                if (LoseNextAcknowledgement) { LoseNextAcknowledgement = false; throw new IOException("Lost acknowledgement."); }
                return new(WopiRevisionWriteStatus.Applied, next);
            }
        }
        public void RemoteWrite(byte[] bytes) { lock (_gate) { Content = bytes; _version++; } }
    }
}
