using System.Text.Json;

namespace SmartPipe.Extensions;

internal static class JsonRecordValidator
{
    public static void Validate(ReadOnlySpan<byte> utf8Json, int maxDepth, string path, long recordIndex)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
            {
                MaxDepth = maxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            while (reader.Read()) { }
        }
        catch (JsonException exception)
        {
            throw new JsonException(
                $"Invalid JSON record {recordIndex} in '{path}': {exception.Message}",
                exception);
        }
    }
}
