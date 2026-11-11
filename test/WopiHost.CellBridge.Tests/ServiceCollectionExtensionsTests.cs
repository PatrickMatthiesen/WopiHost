using System.Security.Claims;
using System.Text;
using FakeItEasy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WopiHost.Abstractions;
using Xunit;

namespace WopiHost.CellBridge.Tests;

public class ServiceCollectionExtensionsTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    [Fact]
    public void AddCellBridgeProcessor_RegistersTheProcessorAsICobaltProcessor()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddCellBridgeProcessor(Configuration());

        using var provider = services.BuildServiceProvider();
        Assert.IsType<CellBridgeProcessor>(provider.GetRequiredService<ICobaltProcessor>());
    }

    [Fact]
    public async Task AddCellBridgeProcessor_DefaultOriginUsesTheCurrentHostRequest()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCellBridgeProcessor(Configuration());
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("wopi.example.test");
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var file = A.Fake<IWopiWritableFile>();
        A.CallTo(() => file.Identifier).Returns("file-1");
        A.CallTo(() => file.Exists).Returns(true);
        A.CallTo(() => file.Extension).Returns("txt");
        A.CallTo(() => file.OpenReadAsync(A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult<Stream>(new MemoryStream("content"u8.ToArray())));
        var body = Encoding.UTF8.GetBytes("""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body>
            <ExecuteCellStorageRequest xmlns="http://schemas.microsoft.com/sharepoint/soap/">
            <RequestVersion Version="2" MinorVersion="2"/><RequestCollection CorrelationId="6b29fc40-ca47-1067-b31d-00dd010662da">
            <Request Url="https://untrusted.example.test/other-file" RequestToken="1">
            <SubRequest Type="WhoAmI" SubRequestToken="1"/></Request></RequestCollection>
            </ExecuteCellStorageRequest></soap:Body></soap:Envelope>
            """);
        var response = await provider.GetRequiredService<ICobaltProcessor>().ProcessCobalt(file,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-user")], "test")), body);
        Assert.Contains("WebUrl=\"https://wopi.example.test\"", Encoding.UTF8.GetString(response));
    }

    [Fact]
    public void AddCellBridgeProcessor_BindsOptionsFromTheWopiCellBridgeSection()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddCellBridgeProcessor(Configuration(
            ("Wopi:CellBridge:SerializationProfile", "Current"),
            ("Wopi:CellBridge:WebOrigin", "https://wopi.example.test")));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CellBridgeProcessorOptions>>().Value;
        Assert.Equal(global::CellBridge.FssHttpB.FsshttpbSerializationProfile.Current, options.SerializationProfile);
        Assert.Equal("https://wopi.example.test", options.WebOrigin);
    }

    [Fact]
    public void AddCellBridgeProcessor_SecondBackend_Throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICobaltProcessor>(_ => throw new InvalidOperationException("never resolved"));

        Assert.Throws<InvalidOperationException>(() => services.AddCellBridgeProcessor(Configuration()));
    }
}
