using System.Buffers;
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

    [Fact]
    public async Task DeadLetterSource_RootArray_AllowsTokenLargerThanReadBuffer()
    {
        var payload = new string('x', 9000);
        var path = await WriteAsync("[" + Serialize(payload, 1) + "]");
        try
        {
            var source = new DeadLetterSource<string>(path, new JsonLinesDeadLetterSerializer<string>(), new() { Format = JsonFileFormat.Array });
            var result = await CollectAsync(source);
            Assert.Equal(payload, Assert.Single(result).Payload);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeadLetterSource_MaxDepth_AllowsBoundary()
    {
        var json = "{\"a\":{\"b\":1}}";
        var reader = new DeadLetterRecordReader<string>();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var records = new List<DeadLetterEnvelope<string>>();
        await foreach (var item in reader.ReadFramedAsync(stream, new SingleSerializer(), new() { Format = JsonFileFormat.Ndjson, MaxDepth = 2 }, null, "memory", TestContext.Current.CancellationToken)) records.Add(item);
        Assert.Single(records);
    }

    [Fact]
    public async Task DeadLetterSource_MaxRecordSize_AllowsExactLimit_AndRejectsLimitPlusOne()
    {
        var line = Serialize("one", 1);
        var size = Encoding.UTF8.GetByteCount(line);
        var exactPath = await WriteAsync(line);
        var oversizedPath = await WriteAsync(line + " ");
        try
        {
            Assert.Single(await CollectAsync(new(exactPath, new JsonLinesDeadLetterSerializer<string>(), new() { Format = JsonFileFormat.Ndjson, MaxRecordSizeBytes = size })));
            await Assert.ThrowsAsync<JsonException>(async () => await CollectAsync(new(oversizedPath, new JsonLinesDeadLetterSerializer<string>(), new() { Format = JsonFileFormat.Ndjson, MaxRecordSizeBytes = size })));
        }
        finally { File.Delete(exactPath); File.Delete(oversizedPath); }
    }

    [Fact]
    public async Task DeadLetterSource_RootArray_AboveUnframedInputLimit_Throws()
    {
        var json = "[" + Serialize("one", 1) + "]";
        var path = await WriteAsync(json);
        try
        {
            await Assert.ThrowsAsync<JsonException>(async () => await CollectAsync(new(path, new JsonLinesDeadLetterSerializer<string>(), new() { Format = JsonFileFormat.Array, MaxUnframedInputSizeBytes = Encoding.UTF8.GetByteCount(json) - 1 })));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeadLetterSource_LegacyTopLevelSequence_TotalAboveLimit_Throws()
    {
        var first = Serialize("one", 1);
        var second = Serialize("two", 2);
        var content = $"{first} {second}";
        var path = await WriteAsync(content);
        try
        {
            var source = new DeadLetterSource<string>(path, DeadLetterSourceTestJsonContext.Default.String, new DeadLetterSourceOptions
            {
                Format = JsonFileFormat.Auto,
                MaxUnframedInputSizeBytes = 1,
            });
            var exception = await Assert.ThrowsAsync<JsonException>(async () => await CollectAsync(source));
            Assert.Contains("configured 1-byte limit", exception.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeadLetterSource_LegacyTopLevelSequence_ExactlyAtLimit_Succeeds()
    {
        var first = Serialize("one", 1);
        var second = Serialize("two", 2);
        var content = $"{first} {second}";
        var path = await WriteAsync(content);
        try
        {
            var source = new DeadLetterSource<string>(path, DeadLetterSourceTestJsonContext.Default.String, new DeadLetterSourceOptions
            {
                Format = JsonFileFormat.Auto,
                MaxUnframedInputSizeBytes = Encoding.UTF8.GetByteCount(content),
            });
            Assert.Equal(2, (await CollectAsync(source)).Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeadLetterSource_LegacyUnframedStream_EnforcesMaxDepth()
    {
        var content = Serialize("one", 1);
        var path = await WriteAsync(content);
        try
        {
            var source = new DeadLetterSource<string>(path, DeadLetterSourceTestJsonContext.Default.String, new DeadLetterSourceOptions
            {
                Format = JsonFileFormat.Auto,
                MaxDepth = 1,
            });
            await Assert.ThrowsAsync<JsonException>(async () => await CollectAsync(source));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeadLetterSource_ExplicitNdjson_SingleElementRootArrayIsInvalid()
    {
        var path = await WriteAsync("[{}]\n");
        try
        {
            var source = new DeadLetterSource<string>(
                path,
                new SingleSerializer(),
                new() { Format = JsonFileFormat.Ndjson });

            await Assert.ThrowsAsync<JsonException>(async () => await CollectAsync(source));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeadLetterSource_ExplicitNdjson_SkipAndLogSkipsRootArray()
    {
        var logger = new CapturingLogger<DeadLetterSource<string>>();
        var path = await WriteAsync("[{}]\n{}\n");
        try
        {
            var source = new DeadLetterSource<string>(
                path,
                new SingleSerializer(),
                new()
                {
                    Format = JsonFileFormat.Ndjson,
                    InvalidRecordBehavior = InvalidJsonRecordBehavior.SkipAndLog,
                },
                logger);

            Assert.Single(await CollectAsync(source));
            Assert.Single(logger.Messages);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LegacyValueTypeInfo_ExplicitArray_NonArrayInput_Throws()
    {
        var path = await WriteAsync(Serialize("one", 1));
        try
        {
            var source = new DeadLetterSource<string>(
                path,
                DeadLetterSourceTestJsonContext.Default.String,
                new() { Format = JsonFileFormat.Array });

            var exception = await Assert.ThrowsAsync<JsonException>(
                async () => await CollectAsync(source));

            Assert.Contains("root JSON array", exception.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LegacyValueTypeInfo_ExplicitArray_ArrayInput_Succeeds()
    {
        var path = await WriteAsync("[" + Serialize("one", 1) + "]");
        try
        {
            var source = new DeadLetterSource<string>(
                path,
                DeadLetterSourceTestJsonContext.Default.String,
                new() { Format = JsonFileFormat.Array });

            Assert.Equal("one", Assert.Single(await CollectAsync(source)).Payload);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LegacyValueTypeInfo_Ndjson_UsesPerRecordLimit()
    {
        var line = Serialize("one", 1);
        var exactSize = Encoding.UTF8.GetByteCount(line);
        var exactPath = await WriteAsync(line);
        var oversizedPath = await WriteAsync(line + " ");
        try
        {
            var exactSource = new DeadLetterSource<string>(
                exactPath,
                DeadLetterSourceTestJsonContext.Default.String,
                new()
                {
                    Format = JsonFileFormat.Ndjson,
                    MaxRecordSizeBytes = exactSize,
                });
            var oversizedSource = new DeadLetterSource<string>(
                oversizedPath,
                DeadLetterSourceTestJsonContext.Default.String,
                new()
                {
                    Format = JsonFileFormat.Ndjson,
                    MaxRecordSizeBytes = exactSize,
                });

            Assert.Equal("one", Assert.Single(await CollectAsync(exactSource)).Payload);
            await Assert.ThrowsAsync<JsonException>(async () => await CollectAsync(oversizedSource));
        }
        finally
        {
            File.Delete(exactPath);
            File.Delete(oversizedPath);
        }
    }

    [Fact]
    public async Task LegacyValueTypeInfo_Ndjson_IgnoresSmallUnframedLimit()
    {
        var path = await WriteAsync(Serialize("one", 1));
        try
        {
            var source = new DeadLetterSource<string>(
                path,
                DeadLetterSourceTestJsonContext.Default.String,
                new()
                {
                    Format = JsonFileFormat.Ndjson,
                    MaxRecordSizeBytes = 1024,
                    MaxUnframedInputSizeBytes = 1,
                });

            Assert.Equal("one", Assert.Single(await CollectAsync(source)).Payload);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LegacyValueTypeInfo_Ndjson_BomCrLfAndLastLineWithoutLf_Succeeds()
    {
        var path = Path.GetTempFileName();
        try
        {
            var content = Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes($"{Serialize("one", 1)}\r\n{Serialize("two", 2)}"))
                .ToArray();
            await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
            var source = new DeadLetterSource<string>(
                path,
                DeadLetterSourceTestJsonContext.Default.String,
                new() { Format = JsonFileFormat.Ndjson });

            Assert.Equal(["one", "two"], (await CollectAsync(source)).Select(item => item.Payload));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeadLetterSource_MaxUnframedInputSize_RejectsLimitPlusOneArray()
    {
        var json = "[" + Serialize("one", 1) + "]";
        var path = await WriteAsync(json);
        try
        {
            await Assert.ThrowsAsync<JsonException>(async () => await CollectAsync(new(path, new JsonLinesDeadLetterSerializer<string>(), new() { Format = JsonFileFormat.Array, MaxUnframedInputSizeBytes = Encoding.UTF8.GetByteCount(json) - 1 })));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeadLetterSource_CustomSerializer_ZeroRecordsIsInvalid()
    {
        var path = await WriteAsync("{}\n");
        try { await Assert.ThrowsAsync<JsonException>(async () => await CollectAsync(new(path, new ZeroSerializer(), new() { Format = JsonFileFormat.Ndjson }))); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeadLetterSource_BomPartialReads_AndLastLineWithoutLf_AreHandled()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Serialize("one", 1))).ToArray();
        await using var stream = new OneByteReadStream(bytes);
        var records = new List<DeadLetterEnvelope<string>>();
        await foreach (var item in new DeadLetterRecordReader<string>().ReadFramedAsync(stream, new JsonLinesDeadLetterSerializer<string>(), new(), null, "memory", TestContext.Current.CancellationToken)) records.Add(item);
        Assert.Equal("one", Assert.Single(records).OriginalPayload);
    }

    [Fact]
    public async Task DeadLetterSource_CancellationDuringOversizedDiscard_ThrowsCancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var stream = new BlockingAfterBytesStream(Encoding.UTF8.GetBytes(new string('x', 32)));
        await using var enumerator = new DeadLetterRecordReader<string>().ReadFramedAsync(
            stream, new ZeroSerializer(), new() { MaxRecordSizeBytes = 8 }, null, "memory", cts.Token).GetAsyncEnumerator(cts.Token);
        var move = enumerator.MoveNextAsync().AsTask();
        await stream.FirstRead.Task.WaitAsync(TestContext.Current.CancellationToken);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
    }

    [Fact]
    public async Task DeadLetterSource_MaxDepth_SourceGeneratedPathMatchesReflectionPath()
    {
        var envelope = new DeadLetterEnvelope<AotDeadLetterSourceItem>
        {
            SchemaVersion = 1,
            PipelineId = "p",
            RunId = "r",
            StageId = "s",
            StageName = "s",
            TraceId = 1,
            OriginalPayload = new(1, "one"),
            Metadata = MetadataBag.Empty,
            Error = new SmartPipeError("failed", ErrorType.Permanent),
            Attempt = 1,
            FailedAtUtc = DateTimeOffset.UnixEpoch,
        };
        var json = JsonSerializer.Serialize(envelope, DeadLetterSourceTestJsonContext.Default.DeadLetterEnvelopeAotDeadLetterSourceItem);
        var path = await WriteAsync(json);
        try
        {
            var options = new DeadLetterSourceOptions { Format = JsonFileFormat.Ndjson, MaxDepth = 1 };
            var reflection = new DeadLetterSource<AotDeadLetterSourceItem>(path, new JsonLinesDeadLetterSerializer<AotDeadLetterSourceItem>(), options);
            var generated = new DeadLetterSource<AotDeadLetterSourceItem>(path, DeadLetterSourceTestJsonContext.Default.DeadLetterEnvelopeAotDeadLetterSourceItem, options);
            await Assert.ThrowsAsync<JsonException>(() => CollectAnyAsync(reflection));
            await Assert.ThrowsAsync<JsonException>(() => CollectAnyAsync(generated));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task JsonDocumentValidator_LongToken_UsesAsyncReadsOfAtMostOneSegment()
    {
        await using var stream = new AsyncOnlyObservingReadStream(Encoding.UTF8.GetBytes("[\"" + new string('x', 40000) + "\"]"));
        await JsonDocumentValidator.ValidateAsync(stream, 64, "memory", TestContext.Current.CancellationToken);
        Assert.InRange(stream.MaximumRequestedRead, 1, JsonDocumentValidator.SegmentSize);
    }

    [Fact]
    public async Task JsonDocumentValidator_AwaitsAsynchronousRead_WithoutFallingBackToSyncRead()
    {
        await using var stream = new SuspendedAsyncOnlyStream(Encoding.UTF8.GetBytes("[]"));
        var validation = JsonDocumentValidator.ValidateAsync(
            stream, 64, "memory", TestContext.Current.CancellationToken).AsTask();

        await stream.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(validation.IsCompleted);
        stream.ReleaseRead();

        await validation;
        Assert.False(stream.SyncReadCalled);
    }

    [Fact]
    public async Task JsonDocumentValidator_ReturnsAndReusesPooledSegments_OnSuccess()
    {
        var pool = new TrackingArrayPool();
        await using var stream = new AsyncOnlyObservingReadStream(Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("123", 10000)) + "]"));

        await JsonDocumentValidator.ValidateAsync(stream, 64, "memory", TestContext.Current.CancellationToken, pool);

        Assert.Equal(pool.RentCount, pool.ReturnCount);
        Assert.True(pool.RentCount > pool.DistinctBufferCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public async Task JsonDocumentValidator_ReturnsPooledSegments_OnJsonException()
    {
        var pool = new TrackingArrayPool();
        await using var stream = new AsyncOnlyObservingReadStream(Encoding.UTF8.GetBytes("[\"unterminated"));

        await Assert.ThrowsAsync<JsonException>(() => JsonDocumentValidator.ValidateAsync(
            stream, 64, "memory", TestContext.Current.CancellationToken, pool).AsTask());

        Assert.Equal(pool.RentCount, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public async Task JsonDocumentValidator_ReturnsPooledSegments_OnCancellation()
    {
        var pool = new TrackingArrayPool();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var stream = new BlockingAfterBytesStream(Encoding.UTF8.GetBytes("[\"unfinished"));
        var validation = JsonDocumentValidator.ValidateAsync(stream, 64, "memory", cts.Token, pool).AsTask();
        await stream.FirstRead.Task.WaitAsync(TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation);
        Assert.Equal(pool.RentCount, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public async Task DeadLetterSource_Auto_BomWhitespaceAndMultiReadRecord_UsesFramedPath()
    {
        var payload = new string('z', 20000);
        var path = Path.GetTempFileName();
        try
        {
            var content = Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(" \r\n\t" + Serialize(payload, 1)))
                .ToArray();
            await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
            var source = new DeadLetterSource<string>(path, new JsonLinesDeadLetterSerializer<string>(), new()
            {
                Format = JsonFileFormat.Auto,
                MaxRecordSizeBytes = content.Length,
                MaxUnframedInputSizeBytes = 1,
            });
            Assert.Equal(payload, Assert.Single(await CollectAsync(source)).Payload);
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

    private static async Task CollectAnyAsync<T>(DeadLetterSource<T> source)
    {
        await foreach (var _ in source.ReadEnvelopesAsync(TestContext.Current.CancellationToken)) { }
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
        SchemaVersion = 1,
        Metadata = MetadataBag.Empty,
        Attempt = 1,
        PipelineId = "pipe",
        RunId = "run",
        StageId = "stage",
        StageName = "stage",
        TraceId = traceId,
        OriginalPayload = payload,
        Error = new SmartPipeError("failed", ErrorType.Permanent),
        FailedAtUtc = DateTimeOffset.UnixEpoch,
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

    private sealed class ZeroSerializer : IDeadLetterSerializer<string>
    {
        public ValueTask WriteAsync(DeadLetterEnvelope<string> envelope, Stream stream, CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<DeadLetterEnvelope<string>> ReadAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.Yield(); yield break; }
    }

    private sealed class SingleSerializer : IDeadLetterSerializer<string>
    {
        public ValueTask WriteAsync(DeadLetterEnvelope<string> envelope, Stream stream, CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<DeadLetterEnvelope<string>> ReadAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.Yield(); yield return (DeadLetterEnvelope<string>)(object)Create("ok", 1); }
    }

    private sealed class OneByteReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class BlockingAfterBytesStream(byte[] bytes) : Stream
    {
        private bool _sent;
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                bytes.CopyTo(buffer);
                FirstRead.TrySetResult();
                return bytes.Length;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class AsyncOnlyObservingReadStream(byte[] bytes) : Stream
    {
        private int _position;
        public int MaximumRequestedRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous reads are forbidden.");
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Synchronous reads are forbidden.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaximumRequestedRead = Math.Max(MaximumRequestedRead, buffer.Length);
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        private readonly Stack<byte[]> _available = new();
        private readonly HashSet<byte[]> _buffers = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<byte[]> _outstanding = new(ReferenceEqualityComparer.Instance);
        public int RentCount { get; private set; }
        public int ReturnCount { get; private set; }
        public int DistinctBufferCount => _buffers.Count;
        public int OutstandingCount => _outstanding.Count;

        public override byte[] Rent(int minimumLength)
        {
            RentCount++;
            var buffer = _available.Count == 0 ? new byte[minimumLength] : _available.Pop();
            _buffers.Add(buffer);
            if (!_outstanding.Add(buffer))
                throw new InvalidOperationException("Pool returned an array that is already outstanding.");
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            if (!_outstanding.Remove(array))
                throw new InvalidOperationException("An unknown or already returned array was returned to the pool.");
            ReturnCount++;
            Array.Fill(array, (byte)0xCC);
            _available.Push(array);
        }
    }

    private sealed class SuspendedAsyncOnlyStream(byte[] bytes) : Stream
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sent;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SyncReadCalled { get; private set; }

        public void ReleaseRead() => _release.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent)
                return 0;
            ReadStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            bytes.CopyTo(buffer);
            _sent = true;
            return bytes.Length;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            SyncReadCalled = true;
            throw new InvalidOperationException("Synchronous reads are forbidden.");
        }

        public override int Read(Span<byte> buffer)
        {
            SyncReadCalled = true;
            throw new InvalidOperationException("Synchronous reads are forbidden.");
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
