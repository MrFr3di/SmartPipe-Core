using System.Text.Json;

namespace SmartPipe.Extensions;

internal static class JsonRecordValidator
{
    public static void Validate(ReadOnlySpan<byte> utf8Json, int maxDepth, string path, long recordIndex)
        => Validate(utf8Json, maxDepth, path, recordIndex, expectedRootTokenType: null);

    public static void ValidateObject(ReadOnlySpan<byte> utf8Json, int maxDepth, string path, long recordIndex)
        => Validate(utf8Json, maxDepth, path, recordIndex, JsonTokenType.StartObject);

    private static void Validate(
        ReadOnlySpan<byte> utf8Json,
        int maxDepth,
        string path,
        long recordIndex,
        JsonTokenType? expectedRootTokenType)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
            {
                MaxDepth = maxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            var isRootToken = true;
            while (reader.Read())
            {
                if (isRootToken && expectedRootTokenType is { } expected && reader.TokenType != expected)
                {
                    throw new JsonException(
                        $"Expected a JSON object record but found {reader.TokenType}.");
                }

                isRootToken = false;
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
