using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Tests;

public class CompressionTransformTests
{
    [Fact]
    public async Task Transform_Brotli_ShouldCompress()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("Hello, SmartPipe! This is test data for compression that should be large enough to compress.");
        var transform = new CompressionTransform(CompressionAlgorithm.Brotli);
        var ctx = new ProcessingContext<byte[]>(data);

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task Transform_GZip_ShouldCompleteWithoutError()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("Hello, SmartPipe! GZip compression test data.");
        var transform = new CompressionTransform(CompressionAlgorithm.GZip);
        var ctx = new ProcessingContext<byte[]>(data);

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
    }
}
