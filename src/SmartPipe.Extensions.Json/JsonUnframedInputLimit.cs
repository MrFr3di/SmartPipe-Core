#nullable enable

using System.Text.Json;
using SmartPipe.Shared;

namespace SmartPipe.Extensions;

/// <summary>Bounds unframed JSON file input and reports a breach as a path-qualified <see cref="JsonException"/>.</summary>
internal static class JsonUnframedInputLimit
{
    /// <summary>Wraps <paramref name="inner"/> without taking ownership of it.</summary>
    public static Stream Create(Stream inner, long maxUnframedInputSizeBytes, string path) =>
        new UnframedInputLimitStream(
            inner,
            maxUnframedInputSizeBytes,
            maximumBytes => new JsonException(
                $"Unframed JSON input '{path}' exceeds the configured {maximumBytes}-byte limit."));
}
