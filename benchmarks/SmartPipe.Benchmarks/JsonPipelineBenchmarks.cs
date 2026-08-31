#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Json;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("JSON")]
public class JsonPipelineBenchmarks
{
    private const int BatchItemCount = 32;
    private const int SinkItemCount = 32;
    private const int OversizedLimit = 128;
    private readonly Dictionary<int, string> _rootPaths = [];
    private readonly Dictionary<int, string> _ndjsonPaths = [];
    private readonly Dictionary<int, string> _boundaryPaths = [];
    private readonly Dictionary<int, int> _boundaryLimits = [];
    private readonly Dictionary<int, string> _oversizedPaths = [];
    private readonly List<string> _definitionPaths = [];
    private string _directory = null!;
    private string _batchPath = null!;
    private string _partialPath = null!;
    private string _sinkPath = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"smartpipe-json-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _batchPath = Path.Combine(_directory, "batch.jsonl");
        _partialPath = Path.Combine(_directory, "partial.jsonl");
        _sinkPath = Path.Combine(_directory, "sink.jsonl");

        foreach (var itemCount in new[] { 1_000, 100_000 })
        {
            var path = Path.Combine(_directory, $"root-{itemCount}.json");
            var items = Enumerable.Range(0, itemCount)
                .Select(static value => new JsonBenchmarkItem(value, "root"))
                .ToList();
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(items, BenchmarkJsonContext.Default.ListJsonBenchmarkItem));
            _rootPaths.Add(itemCount, path);
        }

        foreach (var size in new[] { 64, 1_024, 65_536 })
        {
            var item = CreateItem(size, value: size);
            var record = JsonSerializer.Serialize(item, BenchmarkJsonContext.Default.JsonBenchmarkItem);
            var ndjsonPath = Path.Combine(_directory, $"ndjson-{size}.jsonl");
            await File.WriteAllTextAsync(ndjsonPath, string.Join('\n', Enumerable.Repeat(record, 4)) + "\n");
            _ndjsonPaths.Add(size, ndjsonPath);

            var boundaryPath = Path.Combine(_directory, $"boundary-{size}.jsonl");
            var boundaryBytes = JsonSerializer.SerializeToUtf8Bytes(item, BenchmarkJsonContext.Default.JsonBenchmarkItem);
            await File.WriteAllBytesAsync(boundaryPath, [.. boundaryBytes, (byte)'\n']);
            _boundaryPaths.Add(size, boundaryPath);
            _boundaryLimits.Add(size, boundaryBytes.Length + 1);

        }

        foreach (var size in new[] { 256, 4_096, 65_536 })
        {
            var oversizedPath = Path.Combine(_directory, $"oversized-{size}.jsonl");
            var validRecord = JsonSerializer.Serialize(
                new JsonBenchmarkItem(size, "ok"),
                BenchmarkJsonContext.Default.JsonBenchmarkItem);
            var oversized = new string('x', size) + "\n" + validRecord + "\n";
            await File.WriteAllTextAsync(oversizedPath, oversized);
            _oversizedPaths.Add(size, oversizedPath);
        }

        var batchRecords = Enumerable.Range(0, BatchItemCount)
            .Select(static value => new JsonBenchmarkItem(value, "batch"))
            .ToList();
        var batchJson = JsonSerializer.Serialize(batchRecords, BenchmarkJsonContext.Default.ListJsonBenchmarkItem);
        await File.WriteAllTextAsync(_batchPath, batchJson + "\n" + batchJson + "\n");
        await File.WriteAllTextAsync(_partialPath, string.Join('\n', Enumerable.Repeat(
            JsonSerializer.Serialize(new JsonBenchmarkItem(1, "partial"), BenchmarkJsonContext.Default.JsonBenchmarkItem),
            256)) + "\n");

        for (var index = 0; index < 32; index++)
        {
            var path = Path.Combine(_directory, $"definition-{index}.json");
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(
                    new[] { new JsonBenchmarkItem(index, "definition") },
                    BenchmarkJsonContext.Default.JsonBenchmarkItemArray));
            _definitionPaths.Add(path);
        }

        if (await ReadFileAsync(_rootPaths[1_000], new JsonFileSourceOptions { Format = JsonFileFormat.Array }) != 1_000)
            throw new InvalidOperationException("JSON root-array benchmark setup failed.");
        if (await ReadFileAsync(_batchPath, new JsonFileSourceOptions { Format = JsonFileFormat.BatchJsonLines }) != BatchItemCount * 2)
            throw new InvalidOperationException("JSON batch benchmark setup failed.");
        if (await ReadFileAsync(
                _boundaryPaths[64],
                new JsonFileSourceOptions
                {
                    Format = JsonFileFormat.Ndjson,
                    MaxRecordSizeBytes = _boundaryLimits[64],
                }) != 1)
            throw new InvalidOperationException("JSON boundary benchmark setup failed.");
        if (await ReadFileAsync(
                _oversizedPaths[256],
                new JsonFileSourceOptions
                {
                    Format = JsonFileFormat.Ndjson,
                    InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog,
                    MaxRecordSizeBytes = OversizedLimit,
                }) != 1)
            throw new InvalidOperationException("JSON oversized-discard benchmark setup failed.");

        var definition = JsonPipelineDefinitionBuilder
            .FromJsonFile(
                new PipelineKey("json-benchmark-setup"),
                _rootPaths[1_000],
                BenchmarkJsonContext.Default.JsonBenchmarkItem,
                BenchmarkJsonContext.Default.ListJsonBenchmarkItem,
                new JsonFileSourceOptions { Format = JsonFileFormat.Array })
            .Build();
        if (await RunDefinitionAsync(definition) != 1_000)
            throw new InvalidOperationException("JSON definition benchmark setup failed.");
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [Benchmark]
    [Arguments(1_000)]
    [Arguments(100_000)]
    public Task<int> RootArray_Read(int itemCount) => ReadFileAsync(
        _rootPaths[itemCount],
        new JsonFileSourceOptions { Format = JsonFileFormat.Array });

    [Benchmark]
    [Arguments(64)]
    [Arguments(1_024)]
    [Arguments(65_536)]
    public Task<int> Ndjson_Read_RecordSize(int recordSize) => ReadFileAsync(
        _ndjsonPaths[recordSize],
        new JsonFileSourceOptions { Format = JsonFileFormat.Ndjson });

    [Benchmark]
    public Task<int> BatchJsonLines_Read() => ReadFileAsync(
        _batchPath,
        new JsonFileSourceOptions { Format = JsonFileFormat.BatchJsonLines });

    [Benchmark]
    [Arguments(64)]
    [Arguments(1_024)]
    [Arguments(65_536)]
    public Task<int> MaxRecord_Boundary(int recordSize) => ReadFileAsync(
        _boundaryPaths[recordSize],
        new JsonFileSourceOptions
        {
            Format = JsonFileFormat.Ndjson,
            MaxRecordSizeBytes = _boundaryLimits[recordSize],
        });

    [Benchmark]
    [Arguments(256)]
    [Arguments(4_096)]
    [Arguments(65_536)]
    public Task<int> OversizedDiscard_Scaling(int oversizedSize) => ReadFileAsync(
        _oversizedPaths[oversizedSize],
        new JsonFileSourceOptions
        {
            Format = JsonFileFormat.Ndjson,
            InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog,
            MaxRecordSizeBytes = OversizedLimit,
        });

    [Benchmark]
    public async Task<int> ThirtyTwo_IndependentDefinitionsAndFiles()
    {
        var total = 0;
        for (var index = 0; index < _definitionPaths.Count; index++)
        {
            var definition = JsonPipelineDefinitionBuilder
                .FromJsonFile(
                    new PipelineKey($"json-benchmark-definition-{index}"),
                    _definitionPaths[index],
                    BenchmarkJsonContext.Default.JsonBenchmarkItem,
                    BenchmarkJsonContext.Default.ListJsonBenchmarkItem,
                    new JsonFileSourceOptions { Format = JsonFileFormat.Array })
                .Build();
            total += await RunDefinitionAsync(definition).ConfigureAwait(false);
        }

        return total;
    }

    [Benchmark]
    public async Task<int> PartialEnumeration_DisposesSource()
    {
        await using var source = new JsonFileSource<JsonBenchmarkItem>(
            _partialPath,
            BenchmarkJsonContext.Default.JsonBenchmarkItem,
            BenchmarkJsonContext.Default.ListJsonBenchmarkItem,
            new JsonFileSourceOptions { Format = JsonFileFormat.Ndjson });
        await source.InitializeAsync().ConfigureAwait(false);
        await using var enumerator = source.ReadEnvelopesAsync().GetAsyncEnumerator();
        return await enumerator.MoveNextAsync().ConfigureAwait(false) ? 1 : 0;
    }

    [Benchmark]
    public async Task<int> CancellationAndDisposal_Interaction()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new JsonFileSource<JsonBenchmarkItem>(
            _partialPath,
            BenchmarkJsonContext.Default.JsonBenchmarkItem,
            BenchmarkJsonContext.Default.ListJsonBenchmarkItem,
            new JsonFileSourceOptions { Format = JsonFileFormat.Ndjson });
        try
        {
            await source.InitializeAsync().ConfigureAwait(false);
            var readTask = ConsumeSourceAsync(source, cancellation.Token);
            await Task.Yield();
            var disposalTask = source.DisposeAsync().AsTask();
            await cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(readTask, disposalTask).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return 1;
            }
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Benchmark]
    [Arguments(1, 64)]
    [Arguments(1, 1_024)]
    [Arguments(1, 65_536)]
    [Arguments(1_000, 64)]
    [Arguments(1_000, 1_024)]
    [Arguments(1_000, 65_536)]
    public async Task<int> Sink_AllocationFlushPayload(int flushInterval, int payloadSize)
    {
        var payload = new string('p', payloadSize);
        await using var sink = new JsonFileSink<JsonBenchmarkItem>(
            _sinkPath,
            BenchmarkJsonContext.Default.JsonBenchmarkItem,
            BenchmarkJsonContext.Default.ListJsonBenchmarkItem,
            new JsonFileSinkOptions
            {
                Format = JsonFileFormat.Ndjson,
                OpenMode = JsonFileOpenMode.Create,
                FlushInterval = flushInterval,
            });
        await sink.InitializeAsync().ConfigureAwait(false);
        for (var index = 0; index < SinkItemCount; index++)
        {
            await sink.WriteAsync(
                ProcessingEnvelope<JsonBenchmarkItem>.Create(new JsonBenchmarkItem(index, payload)))
                .ConfigureAwait(false);
        }

        return SinkItemCount;
    }

    private static JsonBenchmarkItem CreateItem(int targetSize, int value)
    {
        var padding = new string('x', Math.Max(0, targetSize - 32));
        return new JsonBenchmarkItem(value, padding);
    }

    private static async Task<int> ReadFileAsync(
        string path,
        JsonFileSourceOptions options,
        CancellationToken cancellationToken = default)
    {
        var logger = options.InvalidRecordBehavior == InvalidJsonRecordBehavior.SkipAndLog
            ? NullLogger<JsonFileSource<JsonBenchmarkItem>>.Instance
            : null;
        await using var source = new JsonFileSource<JsonBenchmarkItem>(
            path,
            BenchmarkJsonContext.Default.JsonBenchmarkItem,
            BenchmarkJsonContext.Default.ListJsonBenchmarkItem,
            options,
            logger);
        await source.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        await foreach (var _ in source.ReadEnvelopesAsync(cancellationToken).ConfigureAwait(false))
            count++;
        return count;
    }

    private static async Task<int> ConsumeSourceAsync(
        JsonFileSource<JsonBenchmarkItem> source,
        CancellationToken cancellationToken)
    {
        var count = 0;
        await foreach (var _ in source.ReadEnvelopesAsync(cancellationToken).ConfigureAwait(false))
            count++;
        return count;
    }

    private static async Task<int> RunDefinitionAsync(
        PipelineDefinition<JsonBenchmarkItem, JsonBenchmarkItem> definition)
    {
        await using var run = await definition.StartAsync().ConfigureAwait(false);
        var count = 0;
        await foreach (var output in run.Outputs.ReadAllAsync().ConfigureAwait(false))
        {
            if (output.Result.IsSuccess)
                count++;
        }

        await run.Completion.ConfigureAwait(false);
        return count;
    }
}

internal sealed record JsonBenchmarkItem(int Value, string Payload);

[JsonSerializable(typeof(JsonBenchmarkItem))]
[JsonSerializable(typeof(JsonBenchmarkItem[]))]
[JsonSerializable(typeof(List<JsonBenchmarkItem>))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
