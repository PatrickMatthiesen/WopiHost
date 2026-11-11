using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Http;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WopiHost.Abstractions;
using Xunit;

namespace WopiHost.CellBridge.Tests;

/// <summary>
/// Drives raw MS-FSSHTTPB cell requests and SOAP cell storage requests through
/// <see cref="CellBridgeProcessor"/> end to end: the WOPI file is imported into cellbridge, queried,
/// locked and saved back. MTOM framing and the exact bytes Office Online Server posts over WOPI are
/// not covered — see the project README.
/// </summary>
public class CellBridgeProcessorTests
{
    private static readonly byte[] s_content = Encoding.UTF8.GetBytes("Hello from WOPI");

    private static CellBridgeProcessor CreateProcessor() =>
        new(NullLogger<CellBridgeProcessor>.Instance,
            Options.Create(new CellBridgeProcessorOptions { SerializationProfile = FsshttpbSerializationProfile.Current, WebOrigin = "https://wopi.example.test" }));

    private static IWopiWritableFile CreateFile(string identifier = "file-1", byte[]? content = null, string extension = "txt")
    {
        var file = A.Fake<IWopiWritableFile>();
        A.CallTo(() => file.Identifier).Returns(identifier);
        A.CallTo(() => file.Exists).Returns(true);
        A.CallTo(() => file.Extension).Returns(extension);
        A.CallTo(() => file.OpenReadAsync(A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(OpenRead(content ?? s_content)));
        return file;
    }

    // The processor owns and disposes the stream it reads.
    private static Stream OpenRead(byte[] content) => new MemoryStream(content, writable: false);

    private static ClaimsPrincipal CreatePrincipal() => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, "user-42"),
        new Claim(ClaimTypes.Name, "Ada Lovelace"),
    ], "test"));

    private static byte[] CellRequest(params FsshttpbCellSubRequest[] subRequests)
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.AddRange(subRequests);
        return request.ToByteArray();
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CellBridgeProcessor(null!, Options.Create(new CellBridgeProcessorOptions())));
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CellBridgeProcessor(NullLogger<CellBridgeProcessor>.Instance, null!));
    }

    [Fact]
    public async Task ProcessCobalt_NullFile_ThrowsArgumentNullException()
    {
        using var processor = CreateProcessor();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => processor.ProcessCobalt(null!, CreatePrincipal(), [], CancellationToken.None));
    }

    [Fact]
    public async Task ProcessCobalt_NullContent_ThrowsArgumentNullException()
    {
        using var processor = CreateProcessor();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => processor.ProcessCobalt(CreateFile(), CreatePrincipal(), null!, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessCobalt_AfterDispose_Throws()
    {
        using var processor = CreateProcessor();
        processor.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => processor.ProcessCobalt(CreateFile(), CreatePrincipal(), [], CancellationToken.None));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var processor = CreateProcessor();
        processor.Dispose();
    }

    [Fact]
    public async Task ProcessCobalt_UnknownFraming_ThrowsNotSupported()
    {
        using var processor = CreateProcessor();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => processor.ProcessCobalt(CreateFile(), CreatePrincipal(), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13], CancellationToken.None));
    }

    [Fact]
    public void Detect_RecognizesSoapAndBinaryFraming()
    {
        Assert.Equal(WopiCobaltFraming.Kind.Soap, WopiCobaltFraming.Detect("  <soap:Envelope/>"u8));
        Assert.Equal(WopiCobaltFraming.Kind.Fsshttpb, WopiCobaltFraming.Detect(CellRequest(new FsshttpbCellSubRequest(RequestTypes.QueryAccess) { RequestId = 1, Data = new QueryAccessSubRequestData() })));
        Assert.Equal(WopiCobaltFraming.Kind.Unknown, WopiCobaltFraming.Detect("PK\u0003\u0004"u8));
        Assert.Equal(WopiCobaltFraming.Kind.Unknown, WopiCobaltFraming.Detect([]));
    }

    [Fact]
    public async Task ProcessCobalt_QueryAccess_GrantsReadAndWrite()
    {
        using var processor = CreateProcessor();
        var body = CellRequest(new FsshttpbCellSubRequest(RequestTypes.QueryAccess) { RequestId = 7, Data = new QueryAccessSubRequestData() });

        var responseBytes = await processor.ProcessCobalt(CreateFile(), CreatePrincipal(), body, CancellationToken.None);

        var response = FsshttpbResponse.Deserialize(new BinaryReaderEx(responseBytes));
        Assert.False(response.Status);
        var sub = Assert.Single(response.SubResponses);
        Assert.Equal(7UL, sub.RequestId);
        Assert.Equal(RequestTypes.QueryAccess, sub.RequestType);
        Assert.False(sub.Status);
        var access = Assert.IsType<QueryAccessSubResponseData>(sub.Data);
        Assert.Null(access.ReadAccessError);
        Assert.Null(access.WriteAccessError);
    }

    [Fact]
    public async Task ProcessCobalt_QueryChanges_ReturnsImportedFileGraph()
    {
        using var processor = CreateProcessor();
        var file = CreateFile();
        var body = CellRequest(new FsshttpbCellSubRequest(RequestTypes.QueryChanges) { RequestId = 3, Data = new QueryChangesSubRequestData() });

        var responseBytes = await processor.ProcessCobalt(file, CreatePrincipal(), body, CancellationToken.None);

        var response = FsshttpbResponse.Deserialize(new BinaryReaderEx(responseBytes));
        var sub = Assert.Single(response.SubResponses);
        Assert.Equal(RequestTypes.QueryChanges, sub.RequestType);
        Assert.False(sub.Status);
        Assert.NotNull(response.DataElementPackage);
        Assert.NotEmpty(response.DataElementPackage.DataElements);
        // The import reads the WOPI file exactly once; queries never touch it again.
        A.CallTo(() => file.OpenReadAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => file.OpenWriteAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessCobalt_SoapWhoAmI_AnswersWithThePrincipal()
    {
        using var processor = CreateProcessor();
        const string envelope = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <ExecuteCellStorageRequest xmlns="http://schemas.microsoft.com/sharepoint/soap/">
                  <RequestVersion Version="2" MinorVersion="2" />
                  <RequestCollection CorrelationId="6B29FC40-CA47-1067-B31D-00DD010662DA">
                    <Request Url="https://wopi.example.test/wopi/file-1.txt" RequestToken="1">
                      <SubRequest Type="WhoAmI" SubRequestToken="1" />
                      <SubRequest Type="ServerTime" SubRequestToken="2" />
                      <SubRequest Type="GetVersions" SubRequestToken="3" />
                    </Request>
                  </RequestCollection>
                </ExecuteCellStorageRequest>
              </soap:Body>
            </soap:Envelope>
            """;

        var responseBytes = await processor.ProcessCobalt(CreateFile(), CreatePrincipal(), Encoding.UTF8.GetBytes(envelope), CancellationToken.None);

        var response = Encoding.UTF8.GetString(responseBytes);
        Assert.Contains("UserName=\"Ada Lovelace\"", response, StringComparison.Ordinal);
        Assert.Contains("UserLogin=\"Ada Lovelace\"", response, StringComparison.Ordinal);
        Assert.Contains("ServerTime=\"", response, StringComparison.Ordinal);
        Assert.Equal("Success", (string?)SubResponse(responseBytes, 3).Attribute("ErrorCode"));
        var versions = SubResponse(responseBytes, 3);
        Assert.Equal("0", (string?)versions.Descendants().Single(e => e.Name.LocalName == "versioning").Attribute("enabled"));
        Assert.Equal("", (string?)versions.Descendants().Single(e => e.Name.LocalName == "settings").Attribute("url"));
        var current = Assert.Single(versions.Descendants(), e => e.Name.LocalName == "result");
        Assert.Equal("https://wopi.example.test/wopi/file-1.txt", (string?)current.Attribute("url"));
        Assert.Equal(s_content.Length.ToString(CultureInfo.InvariantCulture), (string?)current.Attribute("size"));
    }

    [Fact]
    public async Task ProcessCobalt_SameFileTwice_ReusesTheImportedDocument()
    {
        using var processor = CreateProcessor();
        var file = CreateFile();
        var body = CellRequest(new FsshttpbCellSubRequest(RequestTypes.QueryAccess) { RequestId = 1, Data = new QueryAccessSubRequestData() });

        await processor.ProcessCobalt(file, CreatePrincipal(), body, CancellationToken.None);
        await processor.ProcessCobalt(file, CreatePrincipal(), body, CancellationToken.None);

        A.CallTo(() => file.OpenReadAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessCobalt_BinaryRequest_WithUnknownTargetPartition_ThrowsNotSupported()
    {
        using var processor = CreateProcessor();
        var body = CellRequest(new FsshttpbCellSubRequest(RequestTypes.QueryAccess) { RequestId = 1, TargetPartitionId = Guid.NewGuid(), Data = new QueryAccessSubRequestData() });

        await Assert.ThrowsAsync<NotSupportedException>(
            () => processor.ProcessCobalt(CreateFile(), CreatePrincipal(), body, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessCobalt_PutChanges_WritesThePublishedRevisionToTheWopiFile()
    {
        using var processor = CreateProcessor();
        using var written = new MemoryStream();
        // cellbridge accepts a save only as a well-formed Office package for the document's extension.
        var file = CreateFile(content: MinimalDocx("before"), extension: "docx");
        A.CallTo(() => file.OpenWriteAsync(A<CancellationToken>._)).Returns(Task.FromResult<Stream>(written));
        var principal = CreatePrincipal();
        var newContent = MinimalDocx("after");

        // A client learns the document's cell identity and storage index from QueryChanges before it can
        // save; the storage manifest only comes back when asked for, as Office does on open.
        var queryBytes = await processor.ProcessCobalt(file, principal,
            CellRequest(new FsshttpbCellSubRequest(RequestTypes.QueryChanges) { RequestId = 1, Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } }), CancellationToken.None);
        var query = FsshttpbResponse.Deserialize(new BinaryReaderEx(queryBytes));

        var saveBytes = await processor.ProcessCobalt(file, principal, PutChanges(query, requestId: 2, newContent), CancellationToken.None);

        var save = FsshttpbResponse.Deserialize(new BinaryReaderEx(saveBytes));
        var sub = Assert.Single(save.SubResponses);
        Assert.Equal(RequestTypes.PutChanges, sub.RequestType);
        Assert.False(sub.Status, sub.Error?.ErrorMessage);
        A.CallTo(() => file.OpenWriteAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        Assert.Equal(newContent, written.ToArray());
    }

    [Fact]
    public async Task ProcessCobalt_FailedImport_IsRetriedOnTheNextCall()
    {
        using var processor = CreateProcessor();
        var file = CreateFile();
        A.CallTo(() => file.OpenReadAsync(A<CancellationToken>._))
            .Throws(new IOException("storage offline")).Once()
            .Then.ReturnsLazily(() => Task.FromResult(OpenRead(s_content)));
        var body = CellRequest(new FsshttpbCellSubRequest(RequestTypes.QueryAccess) { RequestId = 1, Data = new QueryAccessSubRequestData() });

        await Assert.ThrowsAsync<IOException>(() => processor.ProcessCobalt(file, CreatePrincipal(), body, CancellationToken.None));
        var responseBytes = await processor.ProcessCobalt(file, CreatePrincipal(), body, CancellationToken.None);

        Assert.False(Assert.Single(FsshttpbResponse.Deserialize(new BinaryReaderEx(responseBytes)).SubResponses).Status);
        A.CallTo(() => file.OpenReadAsync(A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task ProcessCobalt_SoapCell_ExecutesTheEmbeddedBinaryRequest()
    {
        using var processor = CreateProcessor();
        var cell = CellRequest(new FsshttpbCellSubRequest(RequestTypes.QueryAccess) { RequestId = 5, Data = new QueryAccessSubRequestData() });
        var body = SoapEnvelope($"""
            <SubRequest Type="Cell" SubRequestToken="1">
              <SubRequestData BinaryDataSize="{cell.Length}">{Convert.ToBase64String(cell)}</SubRequestData>
            </SubRequest>
            """);

        var responseBytes = await processor.ProcessCobalt(CreateFile(), CreatePrincipal(), body, CancellationToken.None);

        var sub = SubResponse(responseBytes, 1);
        Assert.Equal("Success", (string?)sub.Attribute("ErrorCode"));
        var payload = Convert.FromBase64String(SubResponseData(sub).Value);
        var decoded = FsshttpbResponse.Deserialize(new BinaryReaderEx(payload));
        Assert.Equal(FsshttpbCellRequest.CurrentProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(FsshttpbCellRequest.MinimumProtocolVersion, decoded.MinimumVersion);
        var inner = Assert.Single(decoded.SubResponses);
        Assert.Equal(5UL, inner.RequestId);
        Assert.IsType<QueryAccessSubResponseData>(inner.Data);
    }

    [Fact]
    public async Task ProcessCobalt_SoapCell_WithUnknownPartition_IsInvalidArgument()
    {
        using var processor = CreateProcessor();
        var cell = CellRequest(new FsshttpbCellSubRequest(RequestTypes.QueryAccess) { RequestId = 1, Data = new QueryAccessSubRequestData() });
        var body = SoapEnvelope($"""
            <SubRequest Type="Cell" SubRequestToken="1">
              <SubRequestData PartitionID="{Guid.NewGuid():D}" BinaryDataSize="{cell.Length}">{Convert.ToBase64String(cell)}</SubRequestData>
            </SubRequest>
            """);

        var responseBytes = await processor.ProcessCobalt(CreateFile(), CreatePrincipal(), body, CancellationToken.None);

        Assert.Equal("InvalidArgument", (string?)SubResponse(responseBytes, 1).Attribute("ErrorCode"));
    }

    [Fact]
    public async Task ProcessCobalt_SoapCell_WithUndecodablePayload_IsInvalidArgument()
    {
        using var processor = CreateProcessor();
        var body = SoapEnvelope("""
            <SubRequest Type="Cell" SubRequestToken="1">
              <SubRequestData BinaryDataSize="4">not base64</SubRequestData>
            </SubRequest>
            """);

        var responseBytes = await processor.ProcessCobalt(CreateFile(), CreatePrincipal(), body, CancellationToken.None);

        Assert.Equal("InvalidArgument", (string?)SubResponse(responseBytes, 1).Attribute("ErrorCode"));
    }

    [Fact]
    public async Task ProcessCobalt_SoapDependency_OnSuccess_IsNotExecutedAfterAFailure()
    {
        using var processor = CreateProcessor();
        var body = SoapEnvelope("""
            <SubRequest Type="Cell" SubRequestToken="1"><SubRequestData BinaryDataSize="4">not base64</SubRequestData></SubRequest>
            <SubRequest Type="WhoAmI" SubRequestToken="2" DependsOn="1" DependencyType="OnSuccess" />
            """);

        var responseBytes = await processor.ProcessCobalt(CreateFile(), CreatePrincipal(), body, CancellationToken.None);

        Assert.Equal("InvalidArgument", (string?)SubResponse(responseBytes, 1).Attribute("ErrorCode"));
        var dependent = SubResponse(responseBytes, 2);
        Assert.Equal("DependentOnlyOnSuccessRequestFailed", (string?)dependent.Attribute("ErrorCode"));
        Assert.Null(SubResponseData(dependent).Attribute("UserName"));
    }

    [Fact]
    public async Task ProcessCobalt_SoapCoordinationDependencyUsesTheEarlierOuterResult()
    {
        using var processor = CreateProcessor();
        var response = await processor.ProcessCobalt(CreateFile(), CreatePrincipal(), SoapEnvelope($"""
            <SubRequest Type="WhoAmI" SubRequestToken="1" />
            <SubRequest Type="SchemaLock" SubRequestToken="2" DependsOn="1" DependencyType="OnSuccess">
              <SubRequestData SchemaLockRequestType="GetLock" SchemaLockID="{Guid.NewGuid():D}" ClientID="{Guid.NewGuid():D}" Timeout="3600" />
            </SubRequest>
            <SubRequest Type="LockStatus" SubRequestToken="3" DependsOn="2" DependencyType="OnSuccess" />
            """));
        Assert.Equal("Success", (string?)SubResponse(response, 2).Attribute("ErrorCode"));
        Assert.Equal("Success", (string?)SubResponse(response, 3).Attribute("ErrorCode"));
        Assert.Equal("1", (string?)SubResponseData(SubResponse(response, 3)).Attribute("LockType"));
    }

    [Fact]
    public async Task ProcessCobalt_SoapSchemaLock_IsGrantedReportedAndReleased()
    {
        using var processor = CreateProcessor();
        var file = CreateFile();
        var principal = CreatePrincipal();
        var schemaLockId = Guid.NewGuid().ToString("D");
        var clientId = Guid.NewGuid().ToString("D");

        var granted = await processor.ProcessCobalt(file, principal, SoapEnvelope($"""
            <SubRequest Type="SchemaLock" SubRequestToken="1">
              <SubRequestData SchemaLockRequestType="GetLock" SchemaLockID="{schemaLockId}" ClientID="{clientId}" Timeout="3600" />
            </SubRequest>
            <SubRequest Type="LockStatus" SubRequestToken="2" />
            """), CancellationToken.None);

        var lockResponse = SubResponse(granted, 1);
        Assert.Equal("Success", (string?)lockResponse.Attribute("ErrorCode"));
        Assert.Equal("SchemaLock", (string?)SubResponseData(lockResponse).Attribute("LockType"));
        var status = SubResponseData(SubResponse(granted, 2));
        // MS-FSSHTTP LockStatus: 1 = schema lock held.
        Assert.Equal("1", (string?)status.Attribute("LockType"));
        Assert.NotNull(status.Attribute("LockID"));

        var released = await processor.ProcessCobalt(file, principal, SoapEnvelope($"""
            <SubRequest Type="SchemaLock" SubRequestToken="1">
              <SubRequestData SchemaLockRequestType="ReleaseLock" SchemaLockID="{schemaLockId}" ClientID="{clientId}" />
            </SubRequest>
            <SubRequest Type="LockStatus" SubRequestToken="2" />
            """), CancellationToken.None);

        Assert.Equal("Success", (string?)SubResponse(released, 1).Attribute("ErrorCode"));
        // 0 = no lock on the document.
        Assert.Equal("0", (string?)SubResponseData(SubResponse(released, 2)).Attribute("LockType"));
    }

    [Fact]
    public async Task ProcessCobalt_SoapExclusiveLock_BlocksASecondUserOnTheSameDocument()
    {
        using var processor = CreateProcessor();
        var file = CreateFile();
        var other = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "user-99"),
            new Claim(ClaimTypes.Name, "Grace Hopper"),
        ], "test"));

        var first = await processor.ProcessCobalt(file, CreatePrincipal(), ExclusiveLock(Guid.NewGuid()), CancellationToken.None);
        var second = await processor.ProcessCobalt(file, other, ExclusiveLock(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal("Success", (string?)SubResponse(first, 1).Attribute("ErrorCode"));
        Assert.Equal("FileAlreadyLockedOnServer", (string?)SubResponse(second, 1).Attribute("ErrorCode"));
    }

    [Fact]
    public async Task ProcessCobalt_SoapJoinCoauthoring_ReportsASingleEditorAsAlone()
    {
        using var processor = CreateProcessor();
        var file = CreateFile();
        var principal = CreatePrincipal();

        var joined = await processor.ProcessCobalt(file, principal, SoapEnvelope($"""
            <SubRequest Type="Coauth" SubRequestToken="1">
              <SubRequestData CoauthRequestType="JoinCoauthoring" SchemaLockID="{Guid.NewGuid():D}" ClientID="{Guid.NewGuid():D}" Timeout="3600" />
            </SubRequest>
            """), CancellationToken.None);

        var session = SubResponse(joined, 1);
        Assert.Equal("Success", (string?)session.Attribute("ErrorCode"));
        Assert.Equal("Alone", (string?)SubResponseData(session).Attribute("CoauthStatus"));
        var transitionId = (string?)SubResponseData(session).Attribute("TransitionID");
        Assert.NotNull(transitionId);

        var alone = await processor.ProcessCobalt(file, principal, SoapEnvelope($"""
            <SubRequest Type="AmIAlone" SubRequestToken="1">
              <SubRequestData TransitionID="{transitionId}" />
            </SubRequest>
            """), CancellationToken.None);

        Assert.Equal("True", (string?)SubResponseData(SubResponse(alone, 1)).Attribute("AmIAlone"));
    }

    [Theory]
    [InlineData(FsshttpbSerializationProfile.Current)]
    [InlineData(FsshttpbSerializationProfile.SharePoint13_11)]
    public async Task ProcessCobalt_SoapCell_PreservesTheConfiguredSerializationProfile(FsshttpbSerializationProfile profile)
    {
        using var processor = new CellBridgeProcessor(NullLogger<CellBridgeProcessor>.Instance,
            Options.Create(new CellBridgeProcessorOptions { SerializationProfile = profile, WebOrigin = "https://wopi.example.test" }));
        var cell = CellRequest(new FsshttpbCellSubRequest(RequestTypes.QueryAccess) { RequestId = 1, Data = new QueryAccessSubRequestData() });
        var response = await processor.ProcessCobalt(CreateFile(), CreatePrincipal(), SoapEnvelope($"""
            <SubRequest Type="Cell" SubRequestToken="1"><SubRequestData BinaryDataSize="{cell.Length}">{Convert.ToBase64String(cell)}</SubRequestData></SubRequest>
            """));
        var binary = Convert.FromBase64String(SubResponseData(SubResponse(response, 1)).Value);
        Assert.Equal(new FsshttpbResponse().ToByteArray(profile)[..4], binary[..4]);
    }

    [Fact]
    public async Task ProcessCobalt_ConcurrentFirstRequests_ShareTheSameImport()
    {
        using var processor = CreateProcessor();
        var file = CreateFile();
        var import = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => file.OpenReadAsync(A<CancellationToken>._)).Returns(import.Task);
        var first = QueryAsync(processor, file);
        var second = QueryAsync(processor, file);
        A.CallTo(() => file.OpenReadAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        import.SetResult(OpenRead(s_content));
        var responses = await Task.WhenAll(first, second);
        Assert.All(responses, response => Assert.False(Assert.Single(response.SubResponses).Status));
        A.CallTo(() => file.OpenReadAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessCobalt_SoapMetadataAndVersions_ReturnCurrentFileDataAndPermitDependencies()
    {
        using var processor = CreateProcessor();
        var response = await processor.ProcessCobalt(CreateFile(), CreatePrincipal(), SoapEnvelope("""
            <SubRequest Type="GetDocMetaInfo" SubRequestToken="1" />
            <SubRequest Type="GetVersions" SubRequestToken="2" DependsOn="1" DependencyType="OnSuccess" />
            <SubRequest Type="WhoAmI" SubRequestToken="3" DependsOn="2" DependencyType="OnSuccess" />
            <SubRequest Type="FileOperation" SubRequestToken="4"><SubRequestData FileOperation="Rename" NewFileName="renamed.txt" /></SubRequest>
            <SubRequest Type="Versioning" SubRequestToken="5"><SubRequestData VersioningRequestType="RestoreVersion" Version="1.0" /></SubRequest>
            """));
        Assert.Equal("Success", (string?)SubResponse(response, 1).Attribute("ErrorCode"));
        var properties = SubResponse(response, 1).Descendants().Where(e => e.Name.LocalName == "Property")
            .ToDictionary(e => (string)e.Attribute("Key")!, e => (string)e.Attribute("Value")!);
        Assert.Equal(s_content.Length.ToString(CultureInfo.InvariantCulture), properties["vti_filesize"]);
        Assert.Equal("wopi-host", properties["vti_author"]);
        Assert.Equal("Success", (string?)SubResponse(response, 2).Attribute("ErrorCode"));
        Assert.Equal("Ada Lovelace", (string?)SubResponseData(SubResponse(response, 3)).Attribute("UserName"));
        Assert.Equal("NotSupported", (string?)SubResponse(response, 4).Attribute("ErrorCode"));
        Assert.Equal("NotSupported", (string?)SubResponse(response, 5).Attribute("ErrorCode"));
    }

    [Fact]
    public async Task ProcessCobalt_SoapEditorsTable_UpdatesTheBinaryEditorsPartition()
    {
        using var processor = CreateProcessor();
        var file = CreateFile();
        var clientId = Guid.NewGuid();
        var joined = await processor.ProcessCobalt(file, CreatePrincipal(), SoapEnvelope($"""
            <SubRequest Type="EditorsTable" SubRequestToken="1">
              <SubRequestData EditorsTableRequestType="JoinEditingSession" ClientID="{clientId:D}" Timeout="3600" AsEditor="true" />
            </SubRequest>
            """));
        Assert.Equal("Success", (string?)SubResponse(joined, 1).Attribute("ErrorCode"));
        var response = FsshttpbResponse.Deserialize(new BinaryReaderEx(await processor.ProcessCobalt(file, CreatePrincipal(),
            CellRequest(new FsshttpbCellSubRequest(RequestTypes.QueryChanges)
            {
                RequestId = 1, TargetPartitionId = StoredDocument.EditorsTablePartitionId,
                Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true },
            }))));
        var query = Assert.IsType<QueryChangesSubResponseData>(Assert.Single(response.SubResponses).Data);
        var bytes = PartitionGraphSnapshot.Create(response.DataElementPackage!.DataElements, query.StorageIndexExtendedGuid).Materialize();
        var headerLength = EditorsTablePartitionBuilder.ZipStreamHeader.Length;
        using var compressed = new MemoryStream(bytes, headerLength, bytes.Length - headerLength, writable: false);
        using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
        var editor = Assert.Single(XDocument.Load(inflater).Root!.Elements("Editor"));
        Assert.Equal(clientId.ToString("B"), (string?)editor.Element("CacheID"));
    }

    [Fact]
    public async Task ProcessCobalt_SoapOrigin_UsesTheHttpRequestAndEchoesTheClientUrl()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("actual.example.test", 8443);
        using var processor = new CellBridgeProcessor(NullLogger<CellBridgeProcessor>.Instance,
            Options.Create(new CellBridgeProcessorOptions()), new HttpContextAccessor { HttpContext = context });
        var response = XDocument.Parse(Encoding.UTF8.GetString(await processor.ProcessCobalt(CreateFile(), CreatePrincipal(),
            SoapEnvelope("<SubRequest Type=\"WhoAmI\" SubRequestToken=\"1\" />"))));
        Assert.Equal("https://actual.example.test:8443", (string?)response.Descendants().Single(e => e.Name.LocalName == "ResponseCollection").Attribute("WebUrl"));
        Assert.Equal("https://wopi.example.test/wopi/file-1.txt", (string?)response.Descendants().Single(e => e.Name.LocalName == "Response").Attribute("Url"));
    }

    [Fact]
    public async Task ProcessCobalt_SoapSuppliedResourceId_CannotTargetAnotherImportedFile()
    {
        using var processor = CreateProcessor();
        var second = CreateFile("other", Encoding.UTF8.GetBytes("another file"));
        await QueryAsync(processor, second);
        var otherId = (await HeadAsync(processor, second)).ResourceId;
        var body = Encoding.UTF8.GetString(SoapEnvelope("<SubRequest Type=\"GetDocMetaInfo\" SubRequestToken=\"1\" />"))
            .Replace("RequestToken=\"1\">", $"RequestToken=\"1\" UseResourceID=\"true\" ResourceID=\"{otherId:D}\">", StringComparison.Ordinal);
        var response = await processor.ProcessCobalt(CreateFile(), CreatePrincipal(), Encoding.UTF8.GetBytes(body));
        var size = SubResponse(response, 1).Descendants().Single(e => (string?)e.Attribute("Key") == "vti_filesize");
        Assert.Equal(s_content.Length.ToString(CultureInfo.InvariantCulture), (string?)size.Attribute("Value"));
    }

    [Fact]
    public async Task ProcessCobalt_FailedWriteCommit_ReplayedSaveRetriesUntilCommitted()
    {
        using var processor = CreateProcessor();
        var file = CreateFile(content: MinimalDocx("before"), extension: "docx");
        var query = await QueryAsync(processor, file);
        var content = MinimalDocx("after");
        var save = PutChanges(query, 2, content);
        using var failed = new FailingCommitStream();
        using var committed = new MemoryStream();
        A.CallTo(() => file.OpenWriteAsync(A<CancellationToken>._)).Returns(Task.FromResult<Stream>(failed)).Once()
            .Then.Returns(Task.FromResult<Stream>(committed));
        await Assert.ThrowsAsync<IOException>(() => processor.ProcessCobalt(file, CreatePrincipal(), save));
        var response = await processor.ProcessCobalt(file, CreatePrincipal(), save);
        Assert.False(Assert.Single(FsshttpbResponse.Deserialize(new BinaryReaderEx(response)).SubResponses).Status);
        Assert.Equal(content, committed.ToArray());
        await processor.ProcessCobalt(file, CreatePrincipal(), save);
        A.CallTo(() => file.OpenWriteAsync(A<CancellationToken>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task ProcessCobalt_SoapLaterSubrequestFails_StillWritesTheAcceptedSave()
    {
        using var processor = CreateProcessor();
        var file = CreateFile(content: MinimalDocx("before"), extension: "docx");
        var query = await QueryAsync(processor, file);
        var content = MinimalDocx("after");
        var save = PutChanges(query, 2, content);
        using var written = new MemoryStream();
        A.CallTo(() => file.OpenWriteAsync(A<CancellationToken>._)).Returns(Task.FromResult<Stream>(written));
        var response = await processor.ProcessCobalt(file, CreatePrincipal(), SoapEnvelope($"""
            <SubRequest Type="Cell" SubRequestToken="1"><SubRequestData BinaryDataSize="{save.Length}">{Convert.ToBase64String(save)}</SubRequestData></SubRequest>
            <SubRequest Type="Cell" SubRequestToken="2"><SubRequestData BinaryDataSize="4">invalid base64</SubRequestData></SubRequest>
            """));
        Assert.Equal("Success", (string?)SubResponse(response, 1).Attribute("ErrorCode"));
        Assert.Equal("InvalidArgument", (string?)SubResponse(response, 2).Attribute("ErrorCode"));
        Assert.Equal(content, written.ToArray());
        A.CallTo(() => file.OpenWriteAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task MirrorContent_DelayedOlderAcceptedReceipt_DoesNotRewriteTheNewerFile()
    {
        using var processor = CreateProcessor();
        var file = CreateFile(content: MinimalDocx("initial"), extension: "docx");
        var query = await QueryAsync(processor, file);
        var service = Field<CellBridgeDocumentService>(processor, "_documents");
        var head = await HeadAsync(processor, file);
        var actor = new CellBridgeActor(new SubjectIdentity("wopi:user-42", "Ada Lovelace", "Ada Lovelace"));
        // Delay this real accepted receipt at the publication boundary. A newer request
        // completes its WOPI write first, then the older receipt resumes.
        var oldExecution = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents,
            FsshttpbCellRequest.Deserialize(new BinaryReaderEx(PutChanges(query, 2, MinimalDocx("older")))),
            new Dictionary<string, string>(), actor);
        var oldReceipt = Assert.Single(oldExecution.AcceptedSaves);
        var newerContent = MinimalDocx("newer");
        using var written = new MemoryStream();
        A.CallTo(() => file.OpenWriteAsync(A<CancellationToken>._)).Returns(Task.FromResult<Stream>(written));
        await processor.ProcessCobalt(file, CreatePrincipal(), PutChanges(await QueryAsync(processor, file), 3, newerContent));
        var mirror = typeof(CellBridgeProcessor).GetMethod("MirrorContentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)mirror.Invoke(processor, [file, head.ResourceId, ImmutableArray.Create(oldReceipt), CancellationToken.None])!;
        Assert.Equal(newerContent, written.ToArray());
        A.CallTo(() => file.OpenWriteAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    private sealed class FailingCommitStream : MemoryStream
    {
        public override ValueTask DisposeAsync() => ValueTask.FromException(new IOException("commit failed"));
    }

    private static T Field<T>(CellBridgeProcessor processor, string name) =>
        (T)typeof(CellBridgeProcessor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(processor)!;

    private static async Task<DocumentHead> HeadAsync(CellBridgeProcessor processor, IWopiWritableFile file) =>
        (await Field<StorageProvider>(processor, "_storage").Documents.FindAsync(
            new DocumentIdentity.Path(StorageIds.PathKey("/wopi/" + Uri.EscapeDataString(file.Identifier) + "." + file.Extension))))!;

    private static async Task<FsshttpbResponse> QueryAsync(CellBridgeProcessor processor, IWopiWritableFile file) =>
        FsshttpbResponse.Deserialize(new BinaryReaderEx(await processor.ProcessCobalt(file, CreatePrincipal(),
            CellRequest(new FsshttpbCellSubRequest(RequestTypes.QueryChanges)
            {
                RequestId = 1, Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true },
            }))));

    // Mirrors what a client does with a QueryChanges response: reuse the server's cell identity and
    // current storage index, and propose a new storage index over a freshly built content graph.
    private static byte[] PutChanges(FsshttpbResponse query, ulong requestId, byte[] content)
    {
        var queried = Assert.IsType<QueryChangesSubResponseData>(Assert.Single(query.SubResponses).Data);
        var cell = PartitionGraphSnapshot.Create(query.DataElementPackage!.DataElements, queried.StorageIndexExtendedGuid).FileCell;

        static ExGuid Fresh() => new(1, Guid.NewGuid());
        var identity = new StorageManifestBuilder.StableIdentity(Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), cell, Guid.NewGuid());
        var graph = FileContentPartitionBuilder.BuildQueryChangesResponse(requestId, content, identity, queried.CellKnowledgeTo + 1);
        var proposed = ((QueryChangesSubResponseData)graph.SubResponses.Single().Data!).StorageIndexExtendedGuid;

        graph.DataElementPackage!.DataElements.Add(query.DataElementPackage!.DataElements.Single(element =>
            element.DataElementExtendedGuid.Equals(queried.StorageIndexExtendedGuid)));
        var request = new FsshttpbCellRequest { DataElementPackage = graph.DataElementPackage };
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.PutChanges)
        {
            RequestId = requestId,
            Data = new PutChangesSubRequestData { StorageIndex = proposed, ExpectedStorageIndex = queried.StorageIndexExtendedGuid },
        });
        return request.ToByteArray();
    }

    // The smallest OPC package cellbridge's save validation accepts for a .docx: content types plus the
    // main document part.
    private static byte[] MinimalDocx(string text)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open(), new UTF8Encoding(false)))
            {
                writer.Write("""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                      <Default Extension="xml" ContentType="application/xml"/>
                      <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                    </Types>
                    """);
            }

            using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false)))
            {
                writer.Write($"""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                      <w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body>
                    </w:document>
                    """);
            }
        }

        return stream.ToArray();
    }

    private static byte[] ExclusiveLock(Guid lockId) => SoapEnvelope($"""
        <SubRequest Type="ExclusiveLock" SubRequestToken="1">
          <SubRequestData ExclusiveLockRequestType="GetLock" ExclusiveLockID="{lockId:D}" ClientID="{Guid.NewGuid():D}" Timeout="3600" />
        </SubRequest>
        """);

    private static byte[] SoapEnvelope(string subRequests) => Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <ExecuteCellStorageRequest xmlns="http://schemas.microsoft.com/sharepoint/soap/">
              <RequestVersion Version="2" MinorVersion="2" />
              <RequestCollection CorrelationId="6B29FC40-CA47-1067-B31D-00DD010662DA">
                <Request Url="https://wopi.example.test/wopi/file-1.txt" RequestToken="1">
                  {subRequests}
                </Request>
              </RequestCollection>
            </ExecuteCellStorageRequest>
          </soap:Body>
        </soap:Envelope>
        """);

    private static XElement SubResponse(byte[] soapResponse, int subRequestToken) =>
        XDocument.Parse(Encoding.UTF8.GetString(soapResponse)).Descendants()
            .Single(e => e.Name.LocalName == "SubResponse"
                         && (string?)e.Attribute("SubRequestToken") == subRequestToken.ToString(CultureInfo.InvariantCulture));

    private static XElement SubResponseData(XElement subResponse) =>
        subResponse.Elements().Single(e => e.Name.LocalName == "SubResponseData");
}
