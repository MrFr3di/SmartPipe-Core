using System.Net;
using System.Text;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;

namespace SmartPipe.RepositoryChecks.Tests.NuGet;

public sealed class NuGetServiceIndexClientTests
{
    [Theory]
    [InlineData("\"PackageBaseAddress/3.0.0\"")]
    [InlineData("[\"SearchQueryService\",\"PackageBaseAddress/3.0.0\"]")]
    public async Task GetPackageBaseAddressAsync_FindsResource_WhenTypeIsStringOrArray(string resourceType)
    {
        using var httpClient = CreateHttpClient($$"""
            {"resources":[{"@id":"https://packages.example.test/v3-flatcontainer/","@type":{{resourceType}}}]}
            """);
        var client = new NuGetServiceIndexClient(httpClient);

        var result = await client.GetPackageBaseAddressAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://packages.example.test/v3-flatcontainer/"), result);
    }

    [Fact]
    public async Task GetPackageBaseAddressAsync_ThrowsConfigurationError_WhenResourceIsMissing()
    {
        using var httpClient = CreateHttpClient("{\"resources\":[]}");
        var client = new NuGetServiceIndexClient(httpClient);

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => client.GetPackageBaseAddressAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.UsageOrConfigurationError, exception.ExitCode);
        Assert.Contains("PackageBaseAddress/3.0.0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPackageBaseAddressAsync_ThrowsConfigurationError_WhenResourceIsNotHttps()
    {
        using var httpClient = CreateHttpClient(
            "{\"resources\":[{\"@id\":\"http://packages.example.test/\",\"@type\":\"PackageBaseAddress/3.0.0\"}]}");
        var client = new NuGetServiceIndexClient(httpClient);

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => client.GetPackageBaseAddressAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.UsageOrConfigurationError, exception.ExitCode);
        Assert.Contains("HTTPS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPackageBaseAddressAsync_ThrowsExternalSourceError_WhenJsonIsMalformed()
    {
        using var httpClient = CreateHttpClient("not-json");
        var client = new NuGetServiceIndexClient(httpClient);

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => client.GetPackageBaseAddressAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.ExternalSourceUnavailable, exception.ExitCode);
        Assert.Contains("service index", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateHttpClient(string responseBody)
    {
        return new HttpClient(new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            }));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }
}
