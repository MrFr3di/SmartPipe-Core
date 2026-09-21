using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Sinks;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Perf.ExtensionsStrictAb;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
[BenchmarkCategory("Comparative", "StrictAB", "SP220-07")]
public class ExtensionsStrictAbBenchmarks
{
    private const int ChannelItemsPerReader = 64;

    private readonly ProcessingEnvelope<int> _intEnvelope =
        ProcessingEnvelope<int>.Create(42, "perf-sp22007", "run", 1);

    private readonly ProcessingEnvelope<ValidationPayload> _validationEnvelope =
        ProcessingEnvelope<ValidationPayload>.Create(
            new ValidationPayload { Value = 42 },
            "perf-sp22007",
            "run",
            2);

    private readonly byte[] _compressionPayload =
        Enumerable.Range(0, 1024).Select(static index => (byte)(index % 251)).ToArray();

    private FilterTransform<int>? _filter;
    private ConditionalTransform<int>? _conditionalFalse;
    private ConditionalTransform<int>? _conditionalTrue;
    private CompositeTransform<int>? _composite;
    private ValidationTransform<ValidationPayload>? _validation;
    private CompressionTransform? _compression;
    private LoggerSink<int>? _loggerSink;
    private ProcessingEnvelope<byte[]>? _compressionEnvelope;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _filter = new FilterTransform<int>(static value => value > 0);
        _conditionalFalse = new ConditionalTransform<int>(
            static _ => false,
            new IncrementTransform());
        _conditionalTrue = new ConditionalTransform<int>(
            static _ => true,
            new IncrementTransform());
        _composite = new CompositeTransform<int>(
            new IncrementTransform(),
            new IncrementTransform(),
            new IncrementTransform());
        _validation = new ValidationTransform<ValidationPayload>()
            .Require(static value => value.Value % 2 == 0, "Value must be even.");
        _compression = new CompressionTransform(
            CompressionAlgorithm.Brotli,
            CompressionLevel.Fastest);
        _loggerSink = new LoggerSink<int>(NullLogger<LoggerSink<int>>.Instance);
        _compressionEnvelope = ProcessingEnvelope<byte[]>.Create(
            _compressionPayload,
            "perf-sp22007",
            "run",
            3);

        await _filter.InitializeAsync().ConfigureAwait(false);
        await _conditionalFalse.InitializeAsync().ConfigureAwait(false);
        await _conditionalTrue.InitializeAsync().ConfigureAwait(false);
        await _composite.InitializeAsync().ConfigureAwait(false);
        await _validation.InitializeAsync().ConfigureAwait(false);
        await _compression.InitializeAsync().ConfigureAwait(false);
        await _loggerSink.InitializeAsync().ConfigureAwait(false);

        StageResult<int> filter = FilterSyncPass();
        AssertSuccess(filter, 42, nameof(FilterSyncPass));

        StageResult<int> conditionalFalse = ConditionalFalsePassThrough();
        AssertSuccess(conditionalFalse, 42, nameof(ConditionalFalsePassThrough));

        StageResult<int> conditionalTrue = ConditionalTrueTransform();
        AssertSuccess(conditionalTrue, 43, nameof(ConditionalTrueTransform));

        StageResult<int> composite = CompositeThreeTransforms();
        AssertSuccess(composite, 45, nameof(CompositeThreeTransforms));

        StageResult<ValidationPayload> validation = ValidationValid();
        if (!validation.IsSuccess || validation.Value?.Value != 42)
            throw new InvalidOperationException("Validation strict A/B precheck failed.");

        StageResult<byte[]> compression = CompressionBrotli1KiB();
        if (!compression.IsSuccess || compression.Value is null)
            throw new InvalidOperationException("Compression strict A/B precheck failed.");

        byte[] decompressed = DecompressBrotli(compression.Value);
        if (!decompressed.AsSpan().SequenceEqual(_compressionPayload))
            throw new InvalidOperationException("Compression strict A/B precheck changed payload bytes.");

        LoggerSinkLegacyDisabled();

        long channelChecksum = await ChannelMergeTwoReaders().ConfigureAwait(false);
        long expectedChecksum = ExpectedChannelChecksum(ChannelItemsPerReader);
        if (channelChecksum != expectedChecksum)
        {
            throw new InvalidOperationException(
                $"ChannelMerge strict A/B precheck expected checksum {expectedChecksum}, got {channelChecksum}.");
        }
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_loggerSink is not null)
            await _loggerSink.DisposeAsync().ConfigureAwait(false);
        if (_compression is not null)
            await _compression.DisposeAsync().ConfigureAwait(false);
        if (_validation is not null)
            await _validation.DisposeAsync().ConfigureAwait(false);
        if (_composite is not null)
            await _composite.DisposeAsync().ConfigureAwait(false);
        if (_conditionalTrue is not null)
            await _conditionalTrue.DisposeAsync().ConfigureAwait(false);
        if (_conditionalFalse is not null)
            await _conditionalFalse.DisposeAsync().ConfigureAwait(false);
        if (_filter is not null)
            await _filter.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public StageResult<int> FilterSyncPass() =>
        Filter.TransformAsync(_intEnvelope).GetAwaiter().GetResult();

    [Benchmark]
    public StageResult<int> ConditionalFalsePassThrough() =>
        ConditionalFalse.TransformAsync(_intEnvelope).GetAwaiter().GetResult();

    [Benchmark]
    public StageResult<int> ConditionalTrueTransform() =>
        ConditionalTrue.TransformAsync(_intEnvelope).GetAwaiter().GetResult();

    [Benchmark]
    public StageResult<int> CompositeThreeTransforms() =>
        Composite.TransformAsync(_intEnvelope).GetAwaiter().GetResult();

    [Benchmark]
    public StageResult<ValidationPayload> ValidationValid() =>
        Validation.TransformAsync(_validationEnvelope).GetAwaiter().GetResult();

    [Benchmark]
    public StageResult<byte[]> CompressionBrotli1KiB() =>
        Compression.TransformAsync(CompressionEnvelope).GetAwaiter().GetResult();

    [Benchmark]
    public void LoggerSinkLegacyDisabled() =>
        LoggerSink.WriteAsync(_intEnvelope).GetAwaiter().GetResult();

    [Benchmark]
    public async Task<long> ChannelMergeTwoReaders()
    {
        Channel<int> first = Channel.CreateUnbounded<int>();
        Channel<int> second = Channel.CreateUnbounded<int>();

        for (int index = 0; index < ChannelItemsPerReader; index++)
        {
            if (!first.Writer.TryWrite(index))
                throw new InvalidOperationException("Unable to seed first channel.");
            if (!second.Writer.TryWrite(1000 + index))
                throw new InvalidOperationException("Unable to seed second channel.");
        }

        first.Writer.Complete();
        second.Writer.Complete();

        ChannelReader<int> merged = ChannelMerge.Merge(first.Reader, second.Reader);
        long checksum = 0;
        int count = 0;

        await foreach (int item in merged.ReadAllAsync().ConfigureAwait(false))
        {
            checksum += item;
            count++;
        }

        int expectedCount = ChannelItemsPerReader * 2;
        if (count != expectedCount)
        {
            throw new InvalidOperationException(
                $"Merged channel expected {expectedCount} items, got {count}.");
        }

        return checksum;
    }

    private FilterTransform<int> Filter =>
        _filter ?? throw new InvalidOperationException("Filter is not initialized.");

    private ConditionalTransform<int> ConditionalFalse =>
        _conditionalFalse ?? throw new InvalidOperationException("Conditional false transform is not initialized.");

    private ConditionalTransform<int> ConditionalTrue =>
        _conditionalTrue ?? throw new InvalidOperationException("Conditional true transform is not initialized.");

    private CompositeTransform<int> Composite =>
        _composite ?? throw new InvalidOperationException("Composite transform is not initialized.");

    private ValidationTransform<ValidationPayload> Validation =>
        _validation ?? throw new InvalidOperationException("Validation transform is not initialized.");

    private CompressionTransform Compression =>
        _compression ?? throw new InvalidOperationException("Compression transform is not initialized.");

    private LoggerSink<int> LoggerSink =>
        _loggerSink ?? throw new InvalidOperationException("Logger sink is not initialized.");

    private ProcessingEnvelope<byte[]> CompressionEnvelope =>
        _compressionEnvelope ?? throw new InvalidOperationException("Compression envelope is not initialized.");

    private static void AssertSuccess(StageResult<int> result, int expected, string operation)
    {
        if (!result.IsSuccess || result.Value != expected)
        {
            throw new InvalidOperationException(
                $"{operation} strict A/B precheck expected {expected}, got kind={result.Kind}, value={result.Value}.");
        }
    }

    private static byte[] DecompressBrotli(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private static long ExpectedChannelChecksum(int itemsPerReader)
    {
        long sequence = (long)itemsPerReader * (itemsPerReader - 1) / 2;
        return sequence + ((long)itemsPerReader * 1000 + sequence);
    }

    public sealed class ValidationPayload
    {
        [Range(1, 100)]
        public int Value { get; init; }
    }

    private sealed class IncrementTransform : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload + 1));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
