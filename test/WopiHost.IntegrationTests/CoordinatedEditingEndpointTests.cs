using System.Net;
using System.Security.Claims;
using System.Text.Json;
using WopiHost.Abstractions;
using WopiHost.IntegrationTests.Fixtures;
using Xunit;

namespace WopiHost.IntegrationTests;

public sealed class CoordinatedEditingEndpointTests
{
    [Theory]
    [InlineData("/contents", "COBALT")]
    [InlineData("", "RENAME_FILE")]
    [InlineData("", "DELETE")]
    [InlineData("", "PUT_RELATIVE")]
    public async Task LegacyFileMutationsAreBlockedBeforeTheWrite(string suffix, string operation)
    {
        using var host = new Host();
        var (file, token) = await host.CreateFile();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/wopi/files/{file.Identifier}{suffix}?access_token={Uri.EscapeDataString(token)}");
        request.Headers.Add("X-WOPI-Override", operation);
        if (operation is "RENAME_FILE" or "RENAME_CONTAINER") request.Headers.Add("X-WOPI-RequestedName", "renamed");
        request.Content = new ByteArrayContent("replacement"u8.ToArray());
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        Assert.Equal("initial", await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("CREATE_CHILD_FILE")]
    [InlineData("CREATE_CHILD_CONTAINER")]
    [InlineData("DELETE_CONTAINER")]
    [InlineData("RENAME_CONTAINER")]
    public async Task ContainerMutationsCannotBypassSharedAuthority(string operation)
    {
        using var host = new Host();
        var root = host.Factory.Services.GetRequiredService<IWopiStorageProvider>().RootContainer.Identifier;
        var token = await FixtureTokens.MintContainerTokenAsync(host.Factory, Host.User, root);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/wopi/containers/{root}?access_token={Uri.EscapeDataString(token)}");
        request.Headers.Add("X-WOPI-Override", operation);
        if (operation is "RENAME_FILE" or "RENAME_CONTAINER") request.Headers.Add("X-WOPI-RequestedName", "renamed");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Theory]
    [InlineData("LOCK", null, WopiEditingLockOperation.Acquire)]
    [InlineData("LOCK", "old", WopiEditingLockOperation.Replace)]
    [InlineData("UNLOCK", null, WopiEditingLockOperation.Release)]
    [InlineData("REFRESH_LOCK", null, WopiEditingLockOperation.Refresh)]
    [InlineData("GET_LOCK", null, WopiEditingLockOperation.Get)]
    public async Task CorePassesAuthenticatedSubjectAndExpectedTokenToCoordinator(string operation, string? old, WopiEditingLockOperation expected)
    {
        using var host = new Host();
        var (file, token) = await host.CreateFile();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/wopi/files/{file.Identifier}?access_token={Uri.EscapeDataString(token)}");
        request.Headers.Add("X-WOPI-Override", operation);
        if (operation is "RENAME_FILE" or "RENAME_CONTAINER") request.Headers.Add("X-WOPI-RequestedName", "renamed");
        request.Headers.Add("X-WOPI-Lock", "new");
        if (old is not null) request.Headers.Add("X-WOPI-OldLock", old);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, host.Coordinator.Operation);
        Assert.Equal(old ?? "new", host.Coordinator.Token);
        Assert.Equal(old is null ? null : "new", host.Coordinator.Replacement);
        Assert.True(host.Coordinator.Subject?.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task RevocationAtTheCoordinatorReturns403()
    {
        using var host = new Host();
        host.Coordinator.Revoked = true;
        var (file, token) = await host.CreateFile();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/wopi/files/{file.Identifier}?access_token={Uri.EscapeDataString(token)}");
        request.Headers.Add("X-WOPI-Override", "LOCK");
        request.Headers.Add("X-WOPI-Lock", "lock");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CheckFileInfoDoesNotAdvertiseDisabledMutations()
    {
        using var host = new Host();
        var (file, token) = await host.CreateFile();
        using var response = await host.Client.GetAsync($"/wopi/files/{file.Identifier}?access_token={Uri.EscapeDataString(token)}");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("SupportsLocks").GetBoolean());
        Assert.True(json.RootElement.GetProperty("SupportsGetLock").GetBoolean());
        Assert.False(json.RootElement.GetProperty("SupportsUpdate").GetBoolean());
        Assert.False(json.RootElement.GetProperty("SupportsRename").GetBoolean());
        Assert.False(json.RootElement.GetProperty("SupportsDeleteFile").GetBoolean());
    }

    private sealed class Host : IDisposable
    {
        public static readonly FixtureUser User = new("coordinated", "Test", "test@example.com");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "wopi-coordinated-" + Guid.NewGuid().ToString("N"));
        public Coordinator Coordinator { get; } = new();
        public WopiBackendFactory Factory { get; }
        public HttpClient Client { get; }
        public Host()
        {
            Directory.CreateDirectory(_root);
            Factory = new WopiBackendFactory("coordinated-test-secret-32bytes!!", _root,
                services => services.AddSingleton<IWopiEditingCoordinator>(Coordinator));
            Client = Factory.CreateClient();
        }
        public async Task<(IWopiWritableFile, string)> CreateFile()
        {
            var services = Factory.Services;
            var root = services.GetRequiredService<IWopiStorageProvider>().RootContainer.Identifier;
            var file = (await services.GetRequiredService<IWopiWritableStorageProvider>().CreateWopiChildFile(root, "test.txt"))!;
            await using (var stream = await file.OpenWriteAsync()) await stream.WriteAsync("initial"u8.ToArray());
            var token = await FixtureTokens.MintFileTokenAsync(Factory, User, file.Identifier, WopiFilePermissions.UserCanWrite | WopiFilePermissions.UserCanRename);
            return (file, token);
        }
        public void Dispose() { Client.Dispose(); Factory.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    private sealed class Coordinator : IWopiEditingCoordinator
    {
        public WopiEditingLockOperation Operation { get; private set; }
        public string? Token { get; private set; }
        public string? Replacement { get; private set; }
        public ClaimsPrincipal? Subject { get; private set; }
        public bool Revoked { get; set; }
        public Task<WopiEditingLockResult> ProcessLockAsync(IWopiFile file, ClaimsPrincipal principal,
            WopiEditingLockOperation operation, string? token, string? replacementToken = null, CancellationToken cancellationToken = default)
        {
            if (Revoked) throw new UnauthorizedAccessException();
            Operation = operation; Token = token; Replacement = replacementToken; Subject = principal;
            return Task.FromResult(new WopiEditingLockResult(true, "current"));
        }
    }
}
