using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SmartPipe.Extensions.Http.Json;

namespace SmartPipe.Extensions.Http.Json.Tests;

public sealed class HttpJsonResponseReadersTests
{
    [Fact]
    public async Task JsonArray_HandlesEmptySingletonAndMultipleValues()
    {
        var reader = HttpJsonResponseReaders.JsonArray(TestJsonContext.Default.Int32);

        Assert.Empty(await ReadAllAsync(reader, Response("[]")));
        Assert.Equal([7], await ReadAllAsync(reader, Response("[7]")));
        Assert.Equal([7, 8, 9], await ReadAllAsync(reader, Response("[7,8,9]")));
    }

    [Fact]
    public void JsonArrayAndNdjson_RejectNullResponseWhenInvokedBeforeEnumeration()
    {
        var arrayReader = HttpJsonResponseReaders.JsonArray(TestJsonContext.Default.Int32);
        var ndjsonReader = HttpJsonResponseReaders.Ndjson(TestJsonContext.Default.Int32);

        Assert.Equal("response", Assert.Throws<ArgumentNullException>(() => arrayReader(null!, default)).ParamName);
        Assert.Equal("response", Assert.Throws<ArgumentNullException>(() => ndjsonReader(null!, default)).ParamName);
    }

    [Fact]
    public async Task JsonArray_YieldsFirstItemBeforeTheRemainingBodyArrives()
    {
        var stream = new ChunkGateStream(Encoding.UTF8.GetBytes("[1,"), Encoding.UTF8.GetBytes("2]"));
        using var response = Response(stream);
        await using var values = HttpJsonResponseReaders.JsonArray(TestJsonContext.Default.Int32)(response, default)
            .GetAsyncEnumerator();

        Assert.True(await values.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, values.Current);

        stream.ReleaseSecondChunk();
        Assert.True(await values.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, values.Current);
        Assert.True(stream.SecondChunkRequested);
        Assert.False(await values.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task JsonArray_NullItemsThrowByDefaultAndSkipWhenConfigured()
    {
        var response = Response("[\"one\",null,\"two\"]");
        var reader = HttpJsonResponseReaders.JsonArray(TestJsonContext.Default.String);
        await Assert.ThrowsAsync<JsonException>(() => DrainAsync(reader, response));
        response.Dispose();

        using var skipResponse = Response("[\"one\",null,\"two\"]");
        var skipReader = HttpJsonResponseReaders.JsonArray(
            TestJsonContext.Default.String,
            new HttpJsonArrayOptions { NullItemPolicy = HttpJsonNullItemPolicy.Skip });
        Assert.Equal(["one", "two"], await ReadAllAsync(skipReader, skipResponse));
    }

    [Fact]
    public async Task JsonArray_TotalByteLimitAcceptsExactSizeAndCapsThePlusOneProbe()
    {
        var exactStream = new RecordingStream(Encoding.UTF8.GetBytes("[1]"));
        using var exactResponse = Response(exactStream);
        var exactReader = HttpJsonResponseReaders.JsonArray(
            TestJsonContext.Default.Int32,
            new HttpJsonArrayOptions { MaxUnframedBytes = 3 });
        Assert.Equal([1], await ReadAllAsync(exactReader, exactResponse));
        Assert.All(exactStream.RequestedReadSizes, size => Assert.InRange(size, 0, 4));

        var overStream = new RecordingStream(Encoding.UTF8.GetBytes("[1] "));
        using var overResponse = Response(overStream);
        var arrayLimit = await Assert.ThrowsAsync<JsonException>(() => DrainAsync(exactReader, overResponse));
        Assert.Contains("3-byte limit", arrayLimit.Message, StringComparison.Ordinal);
        Assert.NotEmpty(overStream.RequestedReadSizes);
        Assert.All(overStream.RequestedReadSizes, size => Assert.InRange(size, 0, 4));
        Assert.Contains(4, overStream.RequestedReadSizes);
    }

    [Fact]
    public async Task Ndjson_HandlesOneByteUtf8SplitsCrLfBlankLinesAndFinalLineWithoutLf()
    {
        var bytes = Encoding.UTF8.GetBytes("\"α🙂\"\r\n \t\r\n\"終\"");
        var stream = new RecordingStream(bytes, maxChunkSize: 1);
        using var response = Response(stream);
        var reader = HttpJsonResponseReaders.Ndjson(TestJsonContext.Default.String);

        Assert.Equal(["α🙂", "終"], await ReadAllAsync(reader, response));
        Assert.True(stream.RequestedReadSizes.Count > bytes.Length / 2);
    }

    [Fact]
    public async Task Ndjson_ExactRecordLimitPassesAndSkipDrainsOversizedRecordBeforeContinuing()
    {
        var bytes = Encoding.UTF8.GetBytes("\"a\"\n\"oversized\"\n\"b\"");
        var stream = new RecordingStream(bytes, maxChunkSize: 1);
        using var response = Response(stream);
        var reader = HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.String,
            new HttpNdjsonOptions
            {
                MaxRecordSizeBytes = 3,
                OversizeRecordPolicy = HttpJsonOversizeRecordPolicy.Skip,
            });
        await using var values = reader(response, default).GetAsyncEnumerator();

        Assert.True(await values.MoveNextAsync());
        Assert.Equal("a", values.Current);
        Assert.Equal(4, stream.BytesRead);
        Assert.True(await values.MoveNextAsync());
        Assert.Equal("b", values.Current);
        Assert.True(stream.BytesRead >= Encoding.UTF8.GetByteCount("\"a\"\n\"oversized\"\n"));
        Assert.False(await values.MoveNextAsync());

        using var exactResponse = Response("\"x\"\n");
        var exactReader = HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.String,
            new HttpNdjsonOptions { MaxRecordSizeBytes = 3 });
        Assert.Equal(["x"], await ReadAllAsync(exactReader, exactResponse));
    }

    [Fact]
    public async Task Ndjson_OversizeThrowDoesNotIncludeRawRecordText()
    {
        using var response = Response("\"sensitive-record\"\n\"ok\"\n");
        var reader = HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.String,
            new HttpNdjsonOptions { MaxRecordSizeBytes = 3 });

        var exception = await Assert.ThrowsAsync<JsonException>(() => DrainAsync(reader, response));
        Assert.DoesNotContain("sensitive-record", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ndjson_NullRecordsThrowByDefaultAndCanBeSkipped()
    {
        var reader = HttpJsonResponseReaders.Ndjson(TestJsonContext.Default.String);
        using var throwResponse = Response("\"one\"\nnull\n\"two\"");
        await Assert.ThrowsAsync<JsonException>(() => DrainAsync(reader, throwResponse));

        var skipReader = HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.String,
            new HttpNdjsonOptions { NullItemPolicy = HttpJsonNullItemPolicy.Skip });
        using var skipResponse = Response("\"one\"\nnull\n\"two\"");
        Assert.Equal(["one", "two"], await ReadAllAsync(skipReader, skipResponse));
    }

    [Fact]
    public async Task Ndjson_TotalByteLimitAcceptsExactSizeAndCapsThePlusOneProbe()
    {
        var exactStream = new RecordingStream(Encoding.UTF8.GetBytes("1\n"));
        using var exactResponse = Response(exactStream);
        var reader = HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.Int32,
            new HttpNdjsonOptions { MaxUnframedBytes = 2 });
        Assert.Equal([1], await ReadAllAsync(reader, exactResponse));

        var overStream = new RecordingStream(Encoding.UTF8.GetBytes("1\n2"));
        using var overResponse = Response(overStream);
        var ndjsonLimit = await Assert.ThrowsAsync<JsonException>(() => DrainAsync(reader, overResponse));
        Assert.Contains("2-byte limit", ndjsonLimit.Message, StringComparison.Ordinal);
        Assert.NotEmpty(overStream.RequestedReadSizes);
        Assert.All(overStream.RequestedReadSizes, size => Assert.InRange(size, 0, 3));
    }

    [Fact]
    public async Task JsonArrayAndNdjson_EnforceConfiguredDepthAtTheBoundary()
    {
        using var exactArrayResponse = Response("[[1]]");
        var arrayAtDepth = HttpJsonResponseReaders.JsonArray(
            TestJsonContext.Default.Int32Array,
            new HttpJsonArrayOptions { MaxDepth = 2 });
        Assert.Equal([[1]], await ReadAllAsync(arrayAtDepth, exactArrayResponse));

        using var deepArrayResponse = Response("[[1]]");
        var arrayTooShallow = HttpJsonResponseReaders.JsonArray(
            TestJsonContext.Default.Int32Array,
            new HttpJsonArrayOptions { MaxDepth = 1 });
        await Assert.ThrowsAsync<JsonException>(() => DrainAsync(arrayTooShallow, deepArrayResponse));

        using var exactNdjsonResponse = Response("{\"Child\":{\"Value\":1}}\n");
        var ndjsonAtDepth = HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.NestedNode,
            new HttpNdjsonOptions { MaxDepth = 2 });
        Assert.Equal(1, Assert.Single(await ReadAllAsync(ndjsonAtDepth, exactNdjsonResponse)).Child!.Value);

        using var deepNdjsonResponse = Response("{\"Child\":{\"Value\":1}}\n");
        var ndjsonTooShallow = HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.NestedNode,
            new HttpNdjsonOptions { MaxDepth = 1 });
        await Assert.ThrowsAsync<JsonException>(() => DrainAsync(ndjsonTooShallow, deepNdjsonResponse));
    }

    [Fact]
    public async Task Ndjson_CallerCancellationInterruptsBodyReadAndOversizeDiscard()
    {
        using (var cancellation = new CancellationTokenSource())
        {
            var stream = new BlockingReadStream();
            using var response = Response(stream);
            var reader = HttpJsonResponseReaders.Ndjson(TestJsonContext.Default.Int32);
            await using var values = reader(response, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            var move = values.MoveNextAsync().AsTask();
            await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }

        using (var cancellation = new CancellationTokenSource())
        {
            var stream = new PrefixThenBlockingStream(Encoding.UTF8.GetBytes("1234"));
            using var response = Response(stream);
            var reader = HttpJsonResponseReaders.Ndjson(
                TestJsonContext.Default.Int32,
                new HttpNdjsonOptions
                {
                    MaxRecordSizeBytes = 3,
                    OversizeRecordPolicy = HttpJsonOversizeRecordPolicy.Skip,
                });
            await using var values = reader(response, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            var move = values.MoveNextAsync().AsTask();
            await stream.DiscardReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
    }

    [Fact]
    public void Options_RejectInvalidDepthByteAndEnumLimitsWithoutAllocatingLargeRecords()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpJsonResponseReaders.JsonArray(
            TestJsonContext.Default.Int32,
            new HttpJsonArrayOptions { MaxDepth = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpJsonResponseReaders.JsonArray(
            TestJsonContext.Default.Int32,
            new HttpJsonArrayOptions { MaxUnframedBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpJsonResponseReaders.JsonArray(
            TestJsonContext.Default.Int32,
            new HttpJsonArrayOptions { NullItemPolicy = (HttpJsonNullItemPolicy)99 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.Int32,
            new HttpNdjsonOptions { MaxDepth = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.Int32,
            new HttpNdjsonOptions { MaxRecordSizeBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.Int32,
            new HttpNdjsonOptions { MaxRecordSizeBytes = int.MaxValue }));
        Assert.NotNull(HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.Int32,
            new HttpNdjsonOptions { MaxRecordSizeBytes = Array.MaxLength }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.Int32,
            new HttpNdjsonOptions { MaxUnframedBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.Int32,
            new HttpNdjsonOptions { NullItemPolicy = (HttpJsonNullItemPolicy)99 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpJsonResponseReaders.Ndjson(
            TestJsonContext.Default.Int32,
            new HttpNdjsonOptions { OversizeRecordPolicy = (HttpJsonOversizeRecordPolicy)99 }));
    }

    private static HttpResponseMessage Response(string body) =>
        Response(new MemoryStream(Encoding.UTF8.GetBytes(body), writable: false));

    private static HttpResponseMessage Response(Stream stream) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(stream) };

    private static async Task<List<T>> ReadAllAsync<T>(HttpResponseReader<T> reader, HttpResponseMessage response)
    {
        var values = new List<T>();
        await foreach (var value in reader(response, default).ConfigureAwait(false))
            values.Add(value);
        return values;
    }

    private static async Task DrainAsync<T>(HttpResponseReader<T> reader, HttpResponseMessage response)
    {
        await foreach (var _ in reader(response, default).ConfigureAwait(false))
        {
        }
    }

    private sealed class RecordingStream(byte[] data, int maxChunkSize = int.MaxValue) : Stream
    {
        private int _position;
        public List<int> RequestedReadSizes { get; } = [];
        public int BytesRead => _position;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedReadSizes.Add(buffer.Length);
            var count = Math.Min(Math.Min(buffer.Length, maxChunkSize), data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Sync reads are forbidden.");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ChunkGateStream(byte[] first, byte[] second) : Stream
    {
        private int _position;
        private bool _released;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SecondChunkRequested { get; private set; }

        public void ReleaseSecondChunk() => _release.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < first.Length)
                return Copy(first, buffer);
            if (!_released)
            {
                SecondChunkRequested = true;
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                _released = true;
            }
            return _position < first.Length + second.Length ? Copy(second, buffer) : 0;
        }

        private int Copy(byte[] source, Memory<byte> buffer)
        {
            var offset = _position < first.Length ? _position : _position - first.Length;
            var count = Math.Min(buffer.Length, source.Length - offset);
            source.AsMemory(offset, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Sync reads are forbidden.");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => first.Length + second.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Sync reads are forbidden.");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PrefixThenBlockingStream(byte[] prefix) : Stream
    {
        private bool _prefixRead;
        public TaskCompletionSource DiscardReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_prefixRead)
            {
                _prefixRead = true;
                prefix.AsMemory().CopyTo(buffer);
                return prefix.Length;
            }
            DiscardReadStarted.TrySetResult();
            await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Sync reads are forbidden.");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => prefix.Length;
        public override long Position { get => _prefixRead ? prefix.Length : 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => canceled.TrySetResult());
        await canceled.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(NestedNode))]
internal sealed partial class TestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

internal sealed record NestedNode(int? Value = null, NestedNode? Child = null);
