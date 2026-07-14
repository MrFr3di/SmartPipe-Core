using System.Text.Json;
using SmartPipe.Extensions;

namespace SmartPipe.Extensions.Tests;

public sealed class JsonUnframedInputLimitStreamTests
{
    [Fact]
    public void SyncRead_ThrowsAfterLimitAndDoesNotOwnInnerStream()
    {
        var inner = new MemoryStream("123456"u8.ToArray());
        using (var stream = new JsonUnframedInputLimitStream(inner, 5, "sync.json"))
        {
            var exception = Assert.Throws<JsonException>(() => stream.Read(new byte[6], 0, 6));
            AssertLimitException(exception, "sync.json", 5);
        }
        Assert.True(inner.CanRead);
    }

    [Fact]
    public async Task MemoryAsyncRead_EnforcesExactBoundaryAndLimitPlusOne()
    {
        await using var inner = new MemoryStream("123456"u8.ToArray());
        using var stream = new JsonUnframedInputLimitStream(inner, 5, "async.json");
        var buffer = new byte[6];

        await stream.ReadExactlyAsync(
            buffer.AsMemory(0, 5),
            TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<JsonException>(async () =>
            await stream.ReadExactlyAsync(
                buffer.AsMemory(5, 1),
                TestContext.Current.CancellationToken));

        AssertLimitException(exception, "async.json", 5);
    }

    [Fact]
    public async Task LegacyAsyncRead_UsesInnerLegacyOverride_AndEnforcesExactBoundary()
    {
        await using var inner = new LegacyAsyncOnlyStream("123456"u8.ToArray());
        using var stream = new JsonUnframedInputLimitStream(inner, 5, "legacy-async.json");
        var buffer = new byte[6];

        Assert.Equal(5, await stream.ReadAsync(
            buffer,
            0,
            5,
            TestContext.Current.CancellationToken));
        var exception = await Assert.ThrowsAsync<JsonException>(() => stream.ReadAsync(
            buffer,
            5,
            1,
            TestContext.Current.CancellationToken));

        AssertLimitException(exception, "legacy-async.json", 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncRead_PropagatesCancellation(bool useLegacyOverload)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var inner = new CancellableAsyncReadStream();
        using var stream = new JsonUnframedInputLimitStream(inner, 5, "cancel.json");
        var buffer = new byte[1];

        var read = useLegacyOverload
            ? stream.ReadAsync(buffer, 0, buffer.Length, cts.Token)
            : stream.ReadAsync(buffer.AsMemory(), cts.Token).AsTask();
        var readStarted = inner.ReadStarted.Task;
        var firstCompleted = await Task.WhenAny(readStarted, read)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Same(readStarted, firstCompleted);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(useLegacyOverload, inner.LegacyOverloadCalled);
    }

    [Fact]
    public void RewindToStart_ResetsCounter()
    {
        using var inner = new MemoryStream("1234"u8.ToArray());
        using var stream = new JsonUnframedInputLimitStream(inner, 4, "rewind.json");
        Assert.Equal(4, stream.Read(new byte[4], 0, 4));
        Assert.Equal(0, stream.Seek(0, SeekOrigin.Begin));
        Assert.Equal(4, stream.Read(new byte[4], 0, 4));
    }

    [Fact]
    public void NonResetSeek_DoesNotResetCounter()
    {
        using var inner = new MemoryStream("12345"u8.ToArray());
        using var stream = new JsonUnframedInputLimitStream(inner, 5, "seek.json");
        Assert.Equal(3, stream.Read(new byte[3], 0, 3));
        Assert.Equal(2, stream.Seek(-1, SeekOrigin.Current));
        Assert.Throws<JsonException>(() => stream.Read(new byte[3], 0, 3));
    }

    [Fact]
    public void ExactlyAtLimit_DoesNotThrow()
    {
        using var inner = new MemoryStream("12345"u8.ToArray());
        using var stream = new JsonUnframedInputLimitStream(inner, 5, "exact.json");
        Assert.Equal(5, stream.Read(new byte[5], 0, 5));
    }

    private static void AssertLimitException(JsonException exception, string path, long limit)
    {
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"configured {limit}-byte limit", exception.Message, StringComparison.Ordinal);
    }

    private sealed class LegacyAsyncOnlyStream(byte[] bytes) : Stream
    {
        private int _position;

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(count, bytes.Length - _position);
            bytes.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset, read));
            _position += read;
            return Task.FromResult(read);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous reads are forbidden.");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Memory-based async reads are forbidden.");

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellableAsyncReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool LegacyOverloadCalled { get; private set; }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            LegacyOverloadCalled = true;
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous reads are forbidden.");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
