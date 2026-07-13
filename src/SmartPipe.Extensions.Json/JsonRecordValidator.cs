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
            while (reader.Read())
            {
                // Exhaust the reader to validate the complete JSON value,
                // including trailing and structural errors.
            }
        }
        catch (JsonException exception)
        {
            throw new JsonException(
                $"Invalid JSON record {recordIndex} in '{path}': {exception.Message}",
                exception);
        }
    }
}
