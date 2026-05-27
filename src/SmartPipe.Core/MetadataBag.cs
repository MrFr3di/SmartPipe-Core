#nullable enable

using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace SmartPipe.Core;

/// <summary>Immutable metadata collection carried by pipeline envelopes.</summary>
/// <remarks>
/// Keys use ordinal case-sensitive comparison. Values are strings in SmartPipe 1.1.0 so that
/// metadata remains simple to serialize, redact, and expose through diagnostics. Each mutation
/// returns a new <see cref="MetadataBag"/> instance; performance-sensitive callers should avoid
/// adding high-cardinality or large values until benchmark evidence justifies it.
/// </remarks>
public sealed class MetadataBag
{
    private static readonly IReadOnlyDictionary<string, string> EmptyDictionary =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
        );

    private readonly IReadOnlyDictionary<string, string> _items;

    /// <summary>Gets an empty metadata bag.</summary>
    public static MetadataBag Empty { get; } = new(EmptyDictionary);

    /// <summary>Creates an empty metadata bag.</summary>
    public MetadataBag()
        : this(null) { }

    /// <summary>Creates a metadata bag from serialized items.</summary>
    /// <param name="items">Serialized metadata items.</param>
    [JsonConstructor]
    public MetadataBag(IReadOnlyDictionary<string, string>? items)
    {
        _items =
            items is null || items.Count == 0
                ? EmptyDictionary
                : new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(items, StringComparer.Ordinal)
                );
    }

    /// <summary>Gets the serialized metadata items.</summary>
    public IReadOnlyDictionary<string, string> Items => _items;

    /// <summary>Creates a metadata bag from an existing dictionary.</summary>
    /// <param name="metadata">Metadata values to copy.</param>
    /// <returns>A metadata bag containing the copied values.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="metadata"/> is null.</exception>
    public static MetadataBag From(IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Count == 0)
            return Empty;
        return new MetadataBag(metadata);
    }

    /// <summary>Gets a string metadata value.</summary>
    /// <param name="key">Metadata key.</param>
    /// <returns>The value when present; otherwise null.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    public string? GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _items.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>Returns a new metadata bag with the specified value set.</summary>
    /// <param name="key">Metadata key.</param>
    /// <param name="value">Metadata value.</param>
    /// <returns>A new metadata bag with the updated key/value pair.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    public MetadataBag Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        var copy = new Dictionary<string, string>(_items, StringComparer.Ordinal) { [key] = value };
        return new MetadataBag(copy);
    }

    /// <summary>Checks whether a metadata key exists.</summary>
    /// <param name="key">Metadata key.</param>
    /// <returns>True when the key exists; otherwise false.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    public bool Contains(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _items.ContainsKey(key);
    }

    /// <summary>Returns a read-only dictionary view of the metadata.</summary>
    /// <returns>Read-only metadata dictionary.</returns>
    public IReadOnlyDictionary<string, string> AsReadOnlyDictionary() => _items;

    /// <summary>Creates a mutable dictionary copy for legacy APIs.</summary>
    /// <returns>A new mutable dictionary with the current metadata.</returns>
    public Dictionary<string, string> ToDictionary() => new(_items, StringComparer.Ordinal);
}
