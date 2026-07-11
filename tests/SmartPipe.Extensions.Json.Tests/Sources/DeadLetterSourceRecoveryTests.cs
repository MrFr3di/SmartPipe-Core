using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;

namespace SmartPipe.Extensions.Tests.Sources;

[Trait("Category", "CorrectnessRegression")]
public sealed class DeadLetterSourceRecoveryTests
{
    [Fact]
    public async Task DeadLetterSource_SkipAndLog_SkipsMalformedMiddleLine()
    {
        var logger = new CapturingLogger<DeadLetterSource<string>>();
        var json = $"{Serialize("one", 1)}\n{{broken\n{Serialize("two", 2)}\n";
        var items = await ReadAsync(json, new() { Format = JsonFileFormat.Ndjson, InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog }, logger);

        Assert.Equal(["one", "two"], items);
        Assert.Single(logger.Messages);
    }

    [Fact]
    public async Task DeadLetterSource_SkipAndLog_SkipsOversizedMiddleLine()
    {
        var logger = new CapturingLogger<DeadLetterSource<string>>();
        var first = Serialize("one", 1);
        var last = Serialize("two", 2);
        var max = Math.Max(Encoding.UTF8.GetByteCount(first), Encoding.UTF8.GetByteCount(last));
        var json = $"{first}\n{new string('x', max + 1)}\n{last}\n";

        var items = await ReadAsync(json, new() { Format = JsonFileFormat.Ndjson, MaxRecordSizeBytes = max, InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog }, logger);

        Assert.Equal(["one", "two"], items);
        Assert.Single(logger.Messages);
    }

    [Fact]
    public async Task DeadLetterSource_SkipAndLog_SkipsNullPayloadAndContinues()
    {
        var logger = new CapturingLogger<DeadLetterSource<string>>();
        var invalid = JsonSerializer.Serialize(Create(null, 1));
        var items = await ReadAsync($"{invalid}\n{Serialize("two", 2)}", new() { Format = JsonFileFormat.Ndjson, InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog }, logger);

        Assert.Equal(["two"], items);
        Assert.Single(logger.Messages);
    }

    [Fact]
    public void DeadLetterSource_SkipAndLog_ArrayFormatIsRejected()
    {
        var logger = new CapturingLogger<DeadLetterSource<string>>();
        var exception = Assert.Throws<ArgumentException>(() => new DeadLetterSource<string>("x", new JsonLinesDeadLetterSerializer<string>(),
            new() { Format = JsonFileFormat.Array, InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog }, logger));
        Assert.Contains("independently framed", exception.Message);
    }

    [Fact]
    public async Task DeadLetterSource_CustomSerializer_MultipleRecordsIsInvalid()
    {
        var path = await WriteAsync("{}\n");
        try
        {
            var source = new DeadLetterSource<string>(path, new MultipleSerializer(), new() { Format = JsonFileFormat.Ndjson });
            await Assert.ThrowsAsync<JsonException>(async () => await CollectAsync(source));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeadLetterSource_MaxDepth_RejectsFramedRecord()
    {
        var path = await WriteAsync(Serialize("one", 1) + "\n");
        try
        {
            var source = new DeadLetterSource<string>(path, new JsonLinesDeadLetterSerializer<string>(), new() { Format = JsonFileFormat.Ndjson, MaxDepth = 1 });
            await Assert.ThrowsAsync<JsonException>(async () => await CollectAsync(source));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeadLetterSource_MaxDepth_RejectsRootArray()
    {
        var path = await WriteAsync("[" + Serialize("one", 1) + "]");
        try
        {
            var source = new DeadLetterSource<string>(path, new JsonLinesDeadLetterSerializer<string>(), new() { Format = JsonFileFormat.Array, MaxDepth = 1 });
            await Assert.ThrowsAsync<JsonException>(async () => await CollectAsync(source));
        }
        finally { File.Delete(path); }
    }

    private static async Task<string[]> ReadAsync(string json, DeadLetterSourceOptions options, ILogger<DeadLetterSource<string>> logger)
    {
        var path = await WriteAsync(json);
        try { return (await CollectAsync(new DeadLetterSource<string>(path, new JsonLinesDeadLetterSerializer<string>(), options, logger))).Select(x => x.Payload).ToArray(); }
        finally { File.Delete(path); }
    }

    private static async Task<List<ProcessingEnvelope<string>>> CollectAsync(DeadLetterSource<string> source)
    {
        var result = new List<ProcessingEnvelope<string>>();
        await foreach (var item in source.ReadEnvelopesAsync(TestContext.Current.CancellationToken)) result.Add(item);
        return result;
    }

    private static async Task<string> WriteAsync(string text)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        return path;
    }

    private static string Serialize(string payload, ulong traceId) => JsonSerializer.Serialize(Create(payload, traceId));
    private static DeadLetterEnvelope<string?> Create(string? payload, ulong traceId) => new()
    {
        SchemaVersion = 1, Metadata = MetadataBag.Empty, Attempt = 1,
        PipelineId = "pipe", RunId = "run", StageId = "stage", StageName = "stage", TraceId = traceId,
        OriginalPayload = payload, Error = new SmartPipeError("failed", ErrorType.Permanent), FailedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private sealed class MultipleSerializer : IDeadLetterSerializer<string>
    {
        public ValueTask WriteAsync(DeadLetterEnvelope<string> envelope, Stream stream, CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<DeadLetterEnvelope<string>> ReadAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return (DeadLetterEnvelope<string>)(object)Create("one", 1);
            yield return (DeadLetterEnvelope<string>)(object)Create("two", 2);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
