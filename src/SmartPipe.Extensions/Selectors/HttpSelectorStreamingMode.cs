#nullable enable

namespace SmartPipe.Extensions.Selectors;

/// <summary>Streaming response format used by <see cref="HttpSelector{T}"/>.</summary>
public enum HttpSelectorStreamingMode
{
    /// <summary>Stream a JSON array one item at a time.</summary>
    JsonArray = 0,

    /// <summary>Stream newline-delimited JSON, one item per non-empty line.</summary>
    Ndjson = 1,
}
