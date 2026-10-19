#if CELLBRIDGE_WHOLE_FILE_SAVES
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using Npgsql;
using WopiHost.Abstractions;
using WopiHost.CellBridge;
using WopiHost.Core.Models;
using WopiHost.IntegrationTests.Fixtures;
using Xunit;

namespace WopiHost.IntegrationTests;

public sealed class CoordinatedPutFileHttpTests
{
    [Fact]
    public async Task GetLockBeforeEditingDoesNotImportTheDocument()
    {
        using var host = await Host.Create();
        using var response = await host.Request("GET_LOCK", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, Assert.Single(response.Headers.GetValues("X-WOPI-Lock")));
        Assert.Empty(await host.Storage.State.ListAsync(0, 100));
        Assert.Equal(0, host.Destination.ReceiptCount());
    }

    [Fact]
    public async Task PutFilePublishesToTransactionalStorageAndGetReadsTheSameRevision()
    {
        using var host = await Host.Create();
        await host.Lock("held");
        var changed = Package("saved");
        using var response = await host.Put(changed, "held");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(changed, await host.Read());
        Assert.Equal("r1", Assert.Single(response.Headers.GetValues("X-WOPI-ItemVersion")));
        var state = await host.State();
        Assert.Equal(2, state.Revisions.Length);
        await using var historical = await host.Storage.Content.OpenReadAsync(state.Revisions[0].Content);
        using var buffer = new MemoryStream(); await historical.CopyToAsync(buffer);
        Assert.Equal(Package("initial"), buffer.ToArray());
        Assert.Equal(1, host.Destination.ReceiptCount());
        Assert.Equal(0, host.Destination.DirectWrites);
    }

    [Fact]
    public async Task TransientDestinationReadFailureReturns503WithoutImportingOrSaving()
    {
        using var host = await Host.Create();
        host.Destination.FailNextRead = true;
        using var unavailable = await host.Put(Package("saved"), null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Empty(await host.Storage.State.ListAsync(0, 100));
        Assert.Equal(0, host.Destination.ReceiptCount());
        Assert.Equal(Package("initial"), await host.Read());
        await host.Lock("held");
        using var retry = await host.Put(Package("saved"), "held");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(Package("saved"), await host.Read());
        Assert.Equal(1, host.Destination.ReceiptCount());
        Assert.Equal(2, (await host.State()).Revisions.Length);
    }

    [Fact]
    public async Task DestinationDisappearingAfterLookupReturnsConflictWithoutImporting()
    {
        using var host = await Host.Create();
        host.Destination.MissingNextRead = true;
        using var response = await host.Put(Package("refused"), "held");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await host.Storage.State.ListAsync(0, 100));
        Assert.Equal(Package("initial"), await host.Read());
        Assert.Equal(0, host.Destination.ReceiptCount());
    }

    [Fact]
    public async Task CancellingAnActiveBaselineReadPropagatesCancellationWithoutSaving()
    {
        using var host = await Host.Create();
        host.Destination.BlockNextReadUntilCancelled = true;
        using var cancellation = new CancellationTokenSource();
        var pending = host.Put(Package("saved"), null, cancellationToken: cancellation.Token);
        await host.Destination.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await host.Destination.ReadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(await host.Storage.State.ListAsync(0, 100));
        Assert.Equal(0, host.Destination.ReceiptCount());
        Assert.Equal(Package("initial"), await host.Read());
    }

    [Fact]
    public async Task RetryAfterLaterEditPreservesTheLaterEditAndReportsItsCurrentVersion()
    {
        using var host = await Host.Create();
        await host.Lock("held");
        var operation = Guid.NewGuid();
        using var first = await host.Put(Package("first"), "held", operation);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await host.Put(Package("second"), "held");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var retry = await host.Put(Package("first"), "held", operation);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(second.Headers.GetValues("X-WOPI-ItemVersion"), retry.Headers.GetValues("X-WOPI-ItemVersion"));
        Assert.Equal(Package("second"), await host.Read());
        Assert.Equal(2, host.Destination.ReceiptCount());
        Assert.Equal(3, (await host.State()).Revisions.Length);
    }

    [Fact]
    public async Task ChangedRetryPayloadConflictsAndPreservesTheCommittedBytes()
    {
        using var host = await Host.Create(); await host.Lock("held");
        var operation = Guid.NewGuid();
        using var first = await host.Put(Package("first"), "held", operation);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var replacement = await host.Request("LOCK", "current", oldLock: "held");
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        using var changed = await host.Put(Package("different"), "held", operation);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("current", Assert.Single(changed.Headers.GetValues("X-WOPI-Lock")));
        Assert.Equal(Package("first"), await host.Read());
        Assert.Equal(1, host.Destination.ReceiptCount());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostPermissionCeilingAndCurrentPolicyBothDenyPutFile(bool revokePolicy)
    {
        using var host = await Host.Create(); await host.Lock("held");
        if (revokePolicy)
        {
            var state = await host.State();
            Assert.True(await host.Factory.Services.GetRequiredService<CellBridgeDocumentService>().UpdateAuthorizationAsync(
                state.ResourceId, state.Security.AuthorizationPolicy, new(host.Policy.PolicyDomain, 1, 2)));
        }
        else host.PermissionAllowsWrite = false;
        // The signed token still grants writing. The live host permission provider supplies
        // a lower ceiling, which the coordinated processor must carry into CellBridge.
        using var response = await host.Put(Package("denied"), "held");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(Package("initial"), await host.Read());
        Assert.Equal(0, host.Destination.ReceiptCount());
        Assert.Single((await host.State()).Revisions);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("replaced")]
    public async Task InvalidHostLocksCannotSave(string kind)
    {
        using var host = await Host.Create(); await host.Lock("held");
        if (kind == "expired")
        {
            var state = await host.State();
            await host.Storage.State.TransitionAsync(state.ResourceId, (current, now) => new StateTransition<bool>(current with
            { Coordination = current.Coordination with { HostLock = current.Coordination.HostLock! with { ExpiresUtc = now.AddSeconds(-1) } } }, true));
        }
        if (kind == "replaced")
        {
            using var replace = await host.Request("LOCK", "new", oldLock: "held");
            Assert.Equal(HttpStatusCode.OK, replace.StatusCode);
        }
        using var response = await host.Put(Package("refused"), kind == "missing" ? null : kind == "wrong" ? "wrong" : "held");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(Package("initial"), await host.Read());
        Assert.Equal(0, host.Destination.ReceiptCount());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveFsshttpLeaseRejectsPutFileWithoutLeakingItsToken(bool changedRetry)
    {
        using var host = await Host.Create(); await host.Lock("held");
        var operation = Guid.NewGuid();
        if (changedRetry)
        {
            using var first = await host.Put(Package("first"), "held", operation);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        using (var unlock = await host.Request("UNLOCK", "held")) Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var lease = Guid.NewGuid();
        var body = Encoding.UTF8.GetBytes($"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body><ExecuteCellStorageRequest xmlns="http://schemas.microsoft.com/sharepoint/soap/"><RequestVersion Version="2" MinorVersion="2"/><RequestCollection CorrelationId="{Guid.NewGuid():D}"><Request Url="https://wopi.example.test/wopi/files/{SqliteRevisionStore.FileId}" RequestToken="1"><SubRequest Type="ExclusiveLock" SubRequestToken="1"><SubRequestData ExclusiveLockRequestType="GetLock" ExclusiveLockID="{lease:D}" ClientID="{Guid.NewGuid():D}" Timeout="3600"/></SubRequest></Request></RequestCollection></ExecuteCellStorageRequest></soap:Body></soap:Envelope>
            """);
        var file = (await host.Destination.GetWritableFile(SqliteRevisionStore.FileId))!;
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier, Host.User.Id)], "test"));
        var result = await host.Factory.Services.GetRequiredService<ICobaltProcessor>().ProcessCobalt(file, principal, body);
        Assert.Contains("ErrorCode=\"Success\"", Encoding.UTF8.GetString(result));
        using var refused = await host.Put(Package("refused"), changedRetry ? "held" : null, operation);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(string.Empty, Assert.Single(refused.Headers.GetValues("X-WOPI-Lock")));
        Assert.DoesNotContain(lease.ToString(), string.Join("", refused.Headers.SelectMany(h => h.Value)));
        Assert.Equal(Package(changedRetry ? "first" : "initial"), await host.Read());
        Assert.Equal(changedRetry ? 1 : 0, host.Destination.ReceiptCount());
    }

    [Fact]
    public async Task RemoteRevisionConflictPreservesRemoteBytesAndPendingLocalHistory()
    {
        using var host = await Host.Create(); await host.Lock("held");
        host.Destination.RemoteWrite(Package("remote"));
        using var response = await host.Put(Package("local"), "held");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("held", Assert.Single(response.Headers.GetValues("X-WOPI-Lock")));
        Assert.Equal(Package("remote"), await host.Read());
        var state = await host.State();
        Assert.Equal(2, state.Revisions.Length);
        Assert.Single(state.Publication!.Pending);
        Assert.Equal("DestinationConflict", state.Publication.BlockedReason);
        Assert.Equal(0, host.Destination.ReceiptCount());
    }

    [Fact]
    public async Task NoOpSaveStillConflictsWithAnOutOfBandDestinationChange()
    {
        using var host = await Host.Create(); await host.Lock("held");
        host.Destination.RemoteWrite(Package("remote"));
        using var response = await host.Put(Package("initial"), "held");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("held", Assert.Single(response.Headers.GetValues("X-WOPI-Lock")));
        Assert.Equal(Package("remote"), await host.Read());
        Assert.Single((await host.State()).Revisions);
        Assert.Equal(0, host.Destination.ReceiptCount());
    }

    [Fact]
    public async Task UnknownLengthBodyCannotBypassTheHostFileSizeLimit()
    {
        using var host = await Host.Create(maxFileSize: 8); await host.Lock("held");
        // Use the server client to keep the request length unknown.
        using var client = host.Factory.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, host.Url("/contents"));
        request.Headers.Add("X-WOPI-Lock", "held");
        request.Content = new UnknownLengthContent(Package("oversized"));
        Assert.Null(request.Content.Headers.ContentLength);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(Package("initial"), await host.Read());
        Assert.Equal(0, host.Destination.ReceiptCount());
        Assert.Single((await host.State()).Revisions);
    }

    [Fact]
    public async Task CheckContainerInfoDoesNotAdvertiseDisabledMutations()
    {
        using var host = await Host.Create();
        var token = await FixtureTokens.MintContainerTokenAsync(host.Factory, Host.User, "root");
        using var response = await host.Client.GetAsync($"/wopi/containers/root?access_token={Uri.EscapeDataString(token)}");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var name in new[] { "UserCanCreateChildContainer", "UserCanCreateChildFile", "UserCanDelete", "UserCanRename" })
            Assert.False(json.RootElement.GetProperty(name).GetBoolean());
    }

    private sealed class UnknownLengthContent(byte[] bytes) : StreamContent(new UnknownLengthStream(bytes));

    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task LostDestinationAcknowledgementRecoversFromDiskReceiptWithoutASecondWrite()
    {
        using var host = await Host.Create(); await host.Lock("held");
        var operation = Guid.NewGuid(); host.Destination.LoseNextAcknowledgement = true;
        using var first = await host.Put(Package("saved"), "held", operation);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Empty(host.PutFileCallbacks.Calls);
        var reopenedDestination = new SqliteRevisionStore(host.DatabasePath);
        var publisher = new ExternalRevisionPublisher(host.Storage, new WopiRevisionDestination(reopenedDestination));
        await publisher.PublishNextAsync((await host.State()).ResourceId);
        using var retry = await host.Put(Package("saved"), "held", operation, editors: "alice,bob");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(Package("saved"), await host.Read());
        Assert.Equal(1, reopenedDestination.ReceiptCount());
        Assert.Empty((await host.State()).Publication!.Pending);
        Assert.Equal(2, (await host.State()).Revisions.Length);
        var recovered = Assert.Single(host.PutFileCallbacks.Calls);
        Assert.Equal(operation, recovered.OperationId);
        Assert.Equal(Package("saved"), recovered.Bytes);
        Assert.Equal("r1", recovered.Version);
        Assert.Equal(new[] { "alice", "bob" }, recovered.Editors);

        using var duplicateRetry = await host.Put(Package("saved"), "held", operation);
        Assert.Equal(HttpStatusCode.OK, duplicateRetry.StatusCode);
        Assert.Equal(2, host.PutFileCallbacks.Calls.Count);
        Assert.All(host.PutFileCallbacks.Calls, call =>
        {
            Assert.Equal(operation, call.OperationId);
            Assert.Equal("r1", call.Version);
            Assert.Equal(Package("saved"), call.Bytes);
        });
        Assert.Equal(1, reopenedDestination.ReceiptCount());
        Assert.Equal(2, (await host.State()).Revisions.Length);
    }

    [Fact]
    public async Task SuccessfulPutFileInvokesHostCallbackWithUserFileAndEditors()
    {
        using var host = await Host.Create(); await host.Lock("held");
        var operation = Guid.NewGuid();
        using var response = await host.Put(Package("saved"), "held", operation, editors: "alice,bob");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var callback = Assert.Single(host.PutFileCallbacks.Calls);
        Assert.Equal(operation, callback.OperationId);
        Assert.Equal(Host.User.Id, callback.UserId);
        Assert.Equal(SqliteRevisionStore.FileId, callback.FileIdentifier);
        Assert.Equal("test.docx", callback.FileName);
        Assert.Equal(new[] { "alice", "bob" }, callback.Editors);
        Assert.Equal("r1", callback.Version);
        Assert.Equal(Package("saved"), callback.Bytes);
    }

    [Fact]
    public async Task CallbackFailureReturnsServerErrorAndSameOperationRetryInvokesCallbackAgain()
    {
        using var host = await Host.Create(); await host.Lock("held");
        host.PutFileCallbacks.FailNext = true;
        var operation = Guid.NewGuid();

        using var failed = await host.Put(Package("saved"), "held", operation);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(Package("saved"), await host.Read());
        Assert.Equal(1, host.Destination.ReceiptCount());
        Assert.Equal(2, (await host.State()).Revisions.Length);

        using var retry = await host.Put(Package("saved"), "held", operation);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, host.PutFileCallbacks.Calls.Count);
        Assert.All(host.PutFileCallbacks.Calls, call => Assert.Equal(operation, call.OperationId));
        Assert.Equal(1, host.Destination.ReceiptCount());
        Assert.Equal(2, (await host.State()).Revisions.Length);
    }

    [Fact]
    public async Task ConcurrentSavesPreserveEverySuccessfulRevision()
    {
        using var host = await Host.Create(); await host.Lock("held");
        var contents = new[] { Package("one"), Package("two") };
        var responses = await Task.WhenAll(contents.Select(bytes => host.Put(bytes, "held")));
        try
        {
            Assert.All(responses, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
            Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
            var state = await host.State();
            Assert.Equal(1 + responses.Count(r => r.StatusCode == HttpStatusCode.OK), state.Revisions.Length);
            var readBack = await host.Read();
            Assert.Contains(contents, bytes => bytes.SequenceEqual(readBack));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task CheckFileInfoAdvertisesPutFileAndKeepsUnsupportedMutationsDisabled()
    {
        using var host = await Host.Create();
        using var response = await host.Client.GetAsync(host.Url()); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("SupportsUpdate").GetBoolean());
        Assert.True(json.RootElement.GetProperty("SupportsLocks").GetBoolean());
        Assert.True(json.RootElement.GetProperty("UserCanNotWriteRelative").GetBoolean());
        Assert.False(json.RootElement.GetProperty("SupportsRename").GetBoolean());
        Assert.False(json.RootElement.GetProperty("SupportsDeleteFile").GetBoolean());
    }

    [Fact]
    public async Task UnsupportedDocumentFormatIsNotAdvertisedOrWritten()
    {
        using var host = await Host.Create(extension: "txt");
        using var info = await host.Client.GetAsync(host.Url()); info.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await info.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("SupportsUpdate").GetBoolean());
        using var put = await host.Put(Package("unsupported"), null);
        Assert.Equal(HttpStatusCode.NotImplemented, put.StatusCode);
        Assert.Equal(Package("initial"), await host.Read());
        Assert.Equal(0, host.Destination.ReceiptCount());
        Assert.Equal(0, host.Destination.DirectWrites);
    }

    [Fact]
    public async Task ReadOnlyFlagOverridesAnInconsistentWriteGrantInCheckFileInfo()
    {
        using var host = await Host.Create();
        var token = await host.TokenWith(WopiFilePermissions.ReadOnly | WopiFilePermissions.UserCanWrite);
        using var response = await host.Client.GetAsync(host.Url(token: token)); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("UserCanWrite").GetBoolean());
        Assert.True(json.RootElement.GetProperty("ReadOnly").GetBoolean());
        using var denied = await host.Put(Package("denied"), null, token: token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, host.Destination.ReceiptCount());
    }

    [Theory]
    [InlineData("PUT", null)]
    [InlineData("POST", "RENAME_FILE")]
    public async Task SelectedContentsRouteUsesCoordinatedPutFile(string method, string? spoofedOverride)
    {
        using var host = await Host.Create(); await host.Lock("held");
        using var request = new HttpRequestMessage(new HttpMethod(method), host.Url("/contents"));
        request.Content = new ByteArrayContent(Package("saved"));
        request.Headers.Add("X-WOPI-Lock", "held");
        if (spoofedOverride is not null) request.Headers.Add("X-WOPI-Override", spoofedOverride);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Package("saved"), await host.Read());
        Assert.Equal(1, host.Destination.ReceiptCount());
        Assert.Equal(0, host.Destination.DirectWrites);
    }

    [Theory]
    [InlineData("RENAME_FILE")]
    [InlineData("DELETE")]
    [InlineData("PUT_RELATIVE")]
    public async Task OtherFileMutationsRemainExplicitlyDisabled(string operation)
    {
        using var host = await Host.Create();
        using var response = await host.Request(operation, null);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal(Package("initial"), await host.Read());
        Assert.Equal(0, host.Destination.DirectWrites);
    }

    private static byte[] Package(string text)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var types = archive.CreateEntry("[Content_Types].xml"); types.LastWriteTime = DateTimeOffset.UnixEpoch.AddYears(10);
            using (var writer = new StreamWriter(types.Open())) writer.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            var document = archive.CreateEntry("word/document.xml"); document.LastWriteTime = DateTimeOffset.UnixEpoch.AddYears(10);
            using (var writer = new StreamWriter(document.Open())) writer.Write($"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>");
        }
        return buffer.ToArray();
    }

    private sealed class Policy : ICellBridgeAuthorizationPolicy
    {
        public string PolicyDomain => "http-test-policy";
        public DocumentAuthorizationBinding BindNewDocument(Guid resourceId) => new(PolicyDomain, 1, 1);
        public ICellBridgeAuthorizationSnapshot? Resolve(DocumentState state) => new Snapshot(state.ResourceId, state.Security.AuthorizationPolicy!);
        private sealed record Snapshot(Guid ResourceId, DocumentAuthorizationBinding Binding) : ICellBridgeAuthorizationSnapshot
        { public DocumentAccess Evaluate(string subject) => DocumentAccess.Read | (Binding.Revision == 1 ? DocumentAccess.Write : DocumentAccess.None); }
    }

    private sealed class LivePermissions(IWopiPermissionProvider inner, Func<bool> allowWrite) : IWopiPermissionProvider
    {
        public async Task<WopiFilePermissions> GetFilePermissionsAsync(System.Security.Claims.ClaimsPrincipal user, IWopiFile file, CancellationToken cancellationToken = default)
        {
            var permissions = await inner.GetFilePermissionsAsync(user, file, cancellationToken);
            return allowWrite() ? permissions : permissions & ~WopiFilePermissions.UserCanWrite;
        }
        public Task<WopiContainerPermissions> GetContainerPermissionsAsync(System.Security.Claims.ClaimsPrincipal user, IWopiContainer container, CancellationToken cancellationToken = default) => inner.GetContainerPermissionsAsync(user, container, cancellationToken);
    }

    private sealed class Host : IDisposable
    {
        public static readonly FixtureUser User = new("coordinated", "Test", "test@example.com");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "wopi-real-coordinated-" + Guid.NewGuid().ToString("N"));
        private NpgsqlDataSource? _source;
        private string? _schema;
        private string? _connectionString;
        public string DatabasePath => Path.Combine(_root, "destination.db");
        public SqliteRevisionStore Destination { get; private set; } = null!;
        public StorageProvider Storage { get; private set; } = null!;
        public Policy Policy { get; } = new();
        public RecordingPutFileExtensions PutFileCallbacks { get; } = new();
        public bool PermissionAllowsWrite { get; set; } = true;
        public WopiBackendFactory Factory { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public string Token { get; private set; } = "";
        public static async Task<Host> Create(long? maxFileSize = null, string extension = "docx")
        {
            var host = new Host(); Directory.CreateDirectory(host._root);
            host.Destination = new(host.DatabasePath, extension); host.Destination.Initialize(Package("initial"));
            host._connectionString = Environment.GetEnvironmentVariable("WOPI_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(host._connectionString)) host.Storage = new(new InMemoryStateStore(), new InMemoryContentStore());
            else
            {
                host._schema = "http_" + Guid.NewGuid().ToString("N");
                await using (var connection = new NpgsqlConnection(host._connectionString))
                {
                    await connection.OpenAsync();
                    await using var command = new NpgsqlCommand($"CREATE SCHEMA {host._schema}", connection); await command.ExecuteNonQueryAsync();
                }
                var settings = new NpgsqlConnectionStringBuilder(host._connectionString) { SearchPath = host._schema };
                host._source = NpgsqlDataSource.Create(settings.ToString());
                var state = new PostgreSqlStateStore(host._source); await state.InitializeAsync();
                host.Storage = new(state, new PostgreSqlContentStore(host._source));
            }
            host.Factory = new WopiBackendFactory("coordinated-test-secret-32bytes!!", host._root, services =>
            {
                if (maxFileSize is not null) services.Configure<WopiHostOptions>(options => options.MaxFileSize = maxFileSize);
                services.RemoveAll<IWopiStorageProvider>(); services.RemoveAll<IWopiWritableStorageProvider>(); services.RemoveAll<ICobaltProcessor>(); services.RemoveAll<IWopiEditingCoordinator>();
                services.AddSingleton<IWopiStorageProvider>(host.Destination); services.AddSingleton<IWopiWritableStorageProvider>(host.Destination);
                services.AddSingleton(host.Destination); services.AddSingleton(host.Policy);
                services.RemoveAll<IWopiHostExtensions>();
                services.AddSingleton<IWopiHostExtensions>(host.PutFileCallbacks);
                services.RemoveAll<IWopiPermissionProvider>();
                services.AddSingleton<IWopiPermissionProvider>(provider => new LivePermissions(
                    new WopiHost.Core.Security.Authorization.DefaultWopiPermissionProvider(provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<WopiHostOptions>>()),
                    () => host.PermissionAllowsWrite));
                services.AddCellBridge<Policy>(host.Storage, requireDurability: host.Storage.Capabilities.Durable);
                services.AddCoordinatedCellBridgeProcessor<SqliteRevisionStore>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Wopi:CellBridge:Coordinated:ResourceNamespace"] = "8a811ee1-797b-4e63-8595-dfa410e55819",
                    ["Wopi:CellBridge:Coordinated:WebOrigin"] = "https://wopi.example.test",
                    ["Wopi:CellBridge:Coordinated:AllowVolatileProtocolState"] = (!host.Storage.Capabilities.Durable).ToString()
                }).Build());
                // Keep immediate publication, but make retry/recovery scheduling deterministic.
                services.Remove(services.Last(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)));
            });
            host.Client = host.Factory.CreateClient(); host.Token = await host.TokenWith(WopiFilePermissions.UserCanWrite | WopiFilePermissions.UserCanRename);
            return host;
        }
        public Task<string> TokenWith(WopiFilePermissions permissions) => FixtureTokens.MintFileTokenAsync(Factory, User, SqliteRevisionStore.FileId, permissions);
        public string Url(string suffix = "", string? token = null) => $"/wopi/files/{SqliteRevisionStore.FileId}{suffix}?access_token={Uri.EscapeDataString(token ?? Token)}";
        public async Task Lock(string value) { using var response = await Request("LOCK", value); Assert.Equal(HttpStatusCode.OK, response.StatusCode); }
        public async Task<HttpResponseMessage> Request(string operation, string? token, string? oldLock = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url()); request.Headers.Add("X-WOPI-Override", operation);
            if (token is not null) request.Headers.Add("X-WOPI-Lock", token);
            if (oldLock is not null) request.Headers.Add("X-WOPI-OldLock", oldLock);
            if (operation == "RENAME_FILE") request.Headers.Add("X-WOPI-RequestedName", "renamed");
            return await Client.SendAsync(request);
        }
        public async Task<HttpResponseMessage> Put(byte[] bytes, string? lockToken, Guid? operation = null, string? token = null, string? editors = null, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url("/contents", token)); request.Content = new ByteArrayContent(bytes);
            if (lockToken is not null) request.Headers.Add("X-WOPI-Lock", lockToken);
            request.Headers.Add("X-WopiHost-OperationId", (operation ?? Guid.NewGuid()).ToString("D"));
            if (editors is not null) request.Headers.Add("X-WOPI-Editors", editors);
            return await Client.SendAsync(request, cancellationToken);
        }
        public Task<byte[]> Read() => Client.GetByteArrayAsync(Url("/contents"));
        public async Task<DocumentState> State() => (await Storage.State.FindByResourceIdAsync((await Storage.State.ListAsync(0, 100)).Single().ResourceId))!;
        public void Dispose()
        {
            Client?.Dispose(); Factory?.Dispose(); _source?.Dispose();
            if (_schema is not null)
            {
                using var connection = new NpgsqlConnection(_connectionString); connection.Open();
                using var command = new NpgsqlCommand($"DROP SCHEMA {_schema} CASCADE", connection); command.ExecuteNonQuery();
            }
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingPutFileExtensions : WopiHostExtensions
    {
        private readonly object _gate = new();
        private readonly List<PutFileCall> _calls = [];
        private int _failNext;

        public IReadOnlyList<PutFileCall> Calls
        {
            get { lock (_gate) return _calls.ToArray(); }
        }

        public bool FailNext
        {
            get => Volatile.Read(ref _failNext) != 0;
            set => Volatile.Write(ref _failNext, value ? 1 : 0);
        }

        public override async Task OnPutFileAsync(WopiPutFileContext context, CancellationToken cancellationToken = default)
        {
            await using var stream = await context.File.OpenReadAsync(cancellationToken);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            var call = new PutFileCall(
                context.OperationId,
                context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
                context.File.Identifier,
                context.File.Name,
                context.File.Version,
                context.Editors.ToArray(),
                buffer.ToArray());
            lock (_gate) _calls.Add(call);

            if (Interlocked.Exchange(ref _failNext, 0) != 0)
                throw new InvalidOperationException("Configured callback failure for integration test.");
        }
    }

    private sealed record PutFileCall(
        Guid? OperationId,
        string? UserId,
        string FileIdentifier,
        string FileName,
        string? Version,
        string[] Editors,
        byte[] Bytes);
}

#endif
