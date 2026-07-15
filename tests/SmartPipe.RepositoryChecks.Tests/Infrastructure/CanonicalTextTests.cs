using System.Text;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.Tests.Infrastructure;

public sealed class CanonicalTextTests
{
    [Fact]
    public void ToUtf8Bytes_RemovesBomAndNormalizesLineEndingsWithoutTrimming()
    {
        var input = Encoding.UTF8.GetBytes("\uFEFFfirst\r\nsecond\rthird\n\n");

        var result = CanonicalText.ToUtf8Bytes(input);

        Assert.Equal("first\nsecond\nthird\n\n", Encoding.UTF8.GetString(result));
        Assert.False(result.AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public void ToUtf8Bytes_RejectsInvalidUtf8()
    {
        byte[] invalidUtf8 = [0xC3, 0x28];

        Assert.Throws<DecoderFallbackException>(() => CanonicalText.ToUtf8Bytes(invalidUtf8));
    }
}
