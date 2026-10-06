using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SmartPipe.RepositoryChecks.Infrastructure;

internal static class CanonicalJson
{
    public static string Serialize<T>(T value, JsonTypeInfo<T> jsonTypeInfo)
    {
        var json = JsonSerializer.Serialize(value, jsonTypeInfo);
        json = json.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        return json.TrimEnd('\n') + "\n";
    }
}
