using System.Text;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.Tests.Infrastructure;

public sealed class HashingTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void Sha256Hex_ReturnsLowercaseHexadecimal()
    {
        Assert.Equal(AbcSha256, Hashing.Sha256Hex("abc"u8));
    }

    [Fact]
    public async Task Sha256FileAsync_HashesOriginalBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smartpipe-hash-{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("abc"), TestContext.Current.CancellationToken);
        try
        {
            var hash = await Hashing.Sha256FileAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(AbcSha256, hash);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
