#nullable enable

using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Sinks;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("SP220-07", "Channels")]
public class Sp22007ChannelBenchmarks
{
    private const int ItemsPerReader = 128;
    private ChannelReader<int>[] _readers = [];
    private BoundedChannelOptions _boundedOptions = null!;

    [GlobalSetup]
    public void Setup()
    {
        _boundedOptions = new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        };
    }

    [IterationSetup]
    public void PrepareReaders()
    {
        _readers = new ChannelReader<int>[3];
        for (var readerIndex = 0; readerIndex < _readers.Length; readerIndex++)
        {
            var channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });

            for (var itemIndex = 0; itemIndex < ItemsPerReader; itemIndex++)
                channel.Writer.TryWrite((readerIndex * ItemsPerReader) + itemIndex);

            channel.Writer.TryComplete();
            _readers[readerIndex] = channel.Reader;
        }
    }

    [Benchmark]
    public Task<int> MergeMany_ThreeReaders_Bounded() =>
        DrainAsync(
            ChannelMerge.MergeMany(_readers, _boundedOptions, CancellationToken.None),
            _readers.Length * ItemsPerReader);

    [Benchmark]
    public Task<int> MergePair_Unbounded() =>
        DrainAsync(
            ChannelMerge.Merge(_readers[0], _readers[1]),
            2 * ItemsPerReader);

    private static async Task<int> DrainAsync(ChannelReader<int> reader, int expectedCount)
    {
        var count = 0;
        await foreach (var _ in reader.ReadAllAsync().ConfigureAwait(false))
            count++;

        return count == expectedCount
            ? count
            : throw new InvalidOperationException($"Expected {expectedCount} items, received {count}.");
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("SP220-07", "Composite")]
public class Sp22007CompositeBenchmarks
{
    private CompositeTransform<int> _composite = null!;
    private ProcessingEnvelope<int> _envelope = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _composite = new CompositeTransform<int>(
            new AddTransform(1),
            new AddTransform(1));
        _envelope = ProcessingEnvelope<int>.Create(
            40,
            "sp220-07-benchmark",
            "composite",
            1,
            createdAtUtc: DateTimeOffset.UnixEpoch);
        await _composite.InitializeAsync().ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _composite.DisposeAsync().ConfigureAwait(false);

    [Benchmark]
    public async Task<int> Transform_TwoStages()
    {
        var result = await _composite.TransformAsync(_envelope).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value != 42)
            throw new InvalidOperationException("Composite benchmark did not return the expected value 42.");

        return 42;
    }

    private sealed class AddTransform(int amount) : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload + amount));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("SP220-07", "Filter")]
public class Sp22007FilterBenchmarks
{
    private FilterTransform<int> _accepted = null!;
    private FilterTransform<int> _filtered = null!;
    private ProcessingEnvelope<int> _envelope = null!;

    [GlobalSetup]
    public void Setup()
    {
        _accepted = new FilterTransform<int>(static (value, _) => ValueTask.FromResult(value > 0));
        _filtered = new FilterTransform<int>(static (value, _) => ValueTask.FromResult(value < 0));
        _envelope = ProcessingEnvelope<int>.Create(
            1,
            "sp220-07-benchmark",
            "filter",
            1,
            createdAtUtc: DateTimeOffset.UnixEpoch);
    }

    [Benchmark]
    public async Task<bool> Transform_TokenAware_Accepted()
    {
        var result = await _accepted.TransformAsync(_envelope).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException("Accepted filter benchmark returned a non-success result.");

        return true;
    }

    [Benchmark]
    public async Task<bool> Transform_TokenAware_Filtered()
    {
        var result = await _filtered.TransformAsync(_envelope).ConfigureAwait(false);
        if (!result.IsTerminalNonFailure)
            throw new InvalidOperationException("Filtered predicate benchmark returned a non-filtered result.");

        return false;
    }
}

[MemoryDiagnoser]
[BenchmarkCategory("SP220-07", "Logger")]
public class Sp22007LoggerBenchmarks
{
    private ProcessingEnvelope<int> _envelope = null!;
    private LoggerSink<int> _legacy = null!;
    private LoggerSink<int> _safe = null!;

    [GlobalSetup]
    public void Setup()
    {
        var logger = new EnabledNoopLogger<LoggerSink<int>>();
        _legacy = new LoggerSink<int>(logger);
        _safe = new LoggerSink<int>(
            logger,
            new LoggerSinkOptions<int> { PayloadMode = LoggerSinkPayloadMode.None });
        _envelope = ProcessingEnvelope<int>.Create(
            42,
            "sp220-07-benchmark",
            "logger",
            1,
            createdAtUtc: DateTimeOffset.UnixEpoch);
    }

    [Benchmark]
    public void Write_LegacyRaw() => _legacy.WriteAsync(_envelope).GetAwaiter().GetResult();

    [Benchmark]
    public void Write_SafeDefault() => _safe.WriteAsync(_envelope).GetAwaiter().GetResult();

    private sealed class EnabledNoopLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
