using System.Text.Json;
using SmartPipe.Extensions;

namespace SmartPipe.Extensions.Tests;

public sealed class JsonDocumentLimitStreamTests
{
    [Fact]
    public void SyncRead_ThrowsAfterLimitAndDoesNotOwnInnerStream()
    {
        var inner = new MemoryStream("123456"u8.ToArray());
        using (var stream = new JsonDocumentLimitStream(inner, 5, "sync.json"))
            Assert.Throws<JsonException>(() => stream.Read(new byte[6], 0, 6));
        Assert.True(inner.CanRead);
    }

    [Fact]
    public async Task AsyncRead_ThrowsAfterLimit()
    {
        await using var inner = new MemoryStream("123456"u8.ToArray());
        using var stream = new JsonDocumentLimitStream(inner, 5, "async.json");
        var exception = await Assert.ThrowsAsync<JsonException>(async () =>
            await stream.ReadExactlyAsync(new byte[6], TestContext.Current.CancellationToken));
        Assert.Contains("async.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RewindToStart_ResetsCounter()
    {
        using var inner = new MemoryStream("1234"u8.ToArray());
        using var stream = new JsonDocumentLimitStream(inner, 4, "rewind.json");
        Assert.Equal(4, stream.Read(new byte[4], 0, 4));
        Assert.Equal(0, stream.Seek(0, SeekOrigin.Begin));
        Assert.Equal(4, stream.Read(new byte[4], 0, 4));
    }

    [Fact]
    public void NonResetSeek_DoesNotResetCounter()
    {
        using var inner = new MemoryStream("12345"u8.ToArray());
        using var stream = new JsonDocumentLimitStream(inner, 5, "seek.json");
        Assert.Equal(3, stream.Read(new byte[3], 0, 3));
        Assert.Equal(2, stream.Seek(-1, SeekOrigin.Current));
        Assert.Throws<JsonException>(() => stream.Read(new byte[3], 0, 3));
    }
}
