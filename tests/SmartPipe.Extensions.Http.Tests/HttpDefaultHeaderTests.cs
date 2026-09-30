using System.Net;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Http.Tests;

public sealed partial class HttpPipelineComponentsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvalidDefaultHeaders_AreRejectedBeforeSourceOrSinkSend(bool factoryClient, bool sink)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Unsafe", "value\r\nInjected: yes");
        var factory = new RecordingHttpClientFactory(() => client);
        var key = new PipelineKey("default-header-rejection");
        PipelineDefinition<int, int> definition;
        if (sink)
        {
            var builder = PipelineDefinitionBuilder.From(key, RuntimeSource([1]));
            definition = factoryClient
                ? builder.ToHttp(factory, "headers", (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/")))
                : builder.ToHttp(client, (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/")));
        }
        else
        {
            var builder = factoryClient
                ? HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(key, factory, "headers",
                    (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/")),
                    (_, _) => EmptyValues<int>())
                : HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(key, client,
                    (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/")),
                    (_, _) => EmptyValues<int>());
            definition = builder.Build();
        }

        var error = await RunExpectingFailureAndDisposeAsync(definition);

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task RequestHeader_OverridesInvalidDefaultWithoutMutatingBorrowedClient()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal(["safe"], request.Headers.GetValues("X-Unsafe"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = new HttpClient(handler);
        const string invalidDefault = "value\r\nInjected: yes";
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Unsafe", invalidDefault);
        var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(
            new PipelineKey("override-default-header"), client,
            (_, _) =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
                request.Headers.Add("X-Unsafe", "safe");
                return ValueTask.FromResult(request);
            },
            (_, _) => EmptyValues<int>()).Build();

        await RunAndDisposeAsync(definition);

        Assert.Equal(1, handler.SendCount);
        Assert.Equal([invalidDefault], client.DefaultRequestHeaders.GetValues("X-Unsafe"));
    }
}
