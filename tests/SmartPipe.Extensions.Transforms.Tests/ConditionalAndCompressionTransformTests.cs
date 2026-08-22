using System.IO.Compression;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms.Tests;

public sealed class ConditionalAndCompressionTransformTests
{
    [Fact]
    public async Task ConditionalTransform_AppliesOnlyMatchingBranchAndPassesExactToken()
    {
        CancellationToken observed = default;
        var child = new DelegateTransform((envelope, token) =>
        {
            observed = token;
            return ValueTask.FromResult(StageResult<int>.Success(envelope.Payload * 2));
        });
        var transform = new ConditionalTransform<int>(static value => value > 0, child);
        using var cancellation = new CancellationTokenSource();

        StageResult<int> skipped = await transform.TransformAsync(ProcessingEnvelope<int>.Create(0), cancellation.Token);
        StageResult<int> applied = await transform.TransformAsync(ProcessingEnvelope<int>.Create(2), cancellation.Token);

        Assert.Equal(0, skipped.Value);
        Assert.Equal(4, applied.Value);
        Assert.Equal(1, child.TransformCount);
        Assert.Equal(cancellation.Token, observed);
        await transform.InitializeAsync(cancellation.Token);
        await transform.DisposeAsync();
        Assert.Equal(cancellation.Token, child.InitializeToken);
        Assert.Equal(1, child.DisposeCount);
    }

    [Theory]
    [InlineData(CompressionAlgorithm.Brotli)]
    [InlineData(CompressionAlgorithm.GZip)]
    public async Task CompressionTransform_RoundTripsKnownPayload(CompressionAlgorithm algorithm)
    {
        byte[] payload = "SmartPipe deterministic compression payload"u8.ToArray();
        var transform = new CompressionTransform(algorithm);

        StageResult<byte[]> result = await transform.TransformAsync(
            ProcessingEnvelope<byte[]>.Create(payload), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        using var input = new MemoryStream(result.Value!);
        using Stream decompressor = algorithm == CompressionAlgorithm.Brotli
            ? new BrotliStream(input, CompressionMode.Decompress)
            : new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        await decompressor.CopyToAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public void CompressionTransform_RejectsUnknownConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompressionTransform((CompressionAlgorithm)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompressionTransform(level: (CompressionLevel)42));
    }

    private sealed class DelegateTransform(
        Func<ProcessingEnvelope<int>, CancellationToken, ValueTask<StageResult<int>>> transform)
        : IPipelineTransformer<int, int>
    {
        internal int TransformCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal CancellationToken InitializeToken { get; private set; }

        public ValueTask InitializeAsync(CancellationToken ct = default)
        {
            InitializeToken = ct;
            return ValueTask.CompletedTask;
        }

        public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            TransformCount++;
            return transform(envelope, ct);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
