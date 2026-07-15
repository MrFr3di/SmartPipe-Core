using System.Text;

namespace SmartPipe.RepositoryChecks.Infrastructure;

internal static class CanonicalText
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static byte[] ToUtf8Bytes(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return StrictUtf8.GetBytes(input);
    }

    public static byte[] ToUtf8Bytes(ReadOnlySpan<byte> input)
    {
        var text = StrictUtf8.GetString(input);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

        return StrictUtf8.GetBytes(text);
    }
}
