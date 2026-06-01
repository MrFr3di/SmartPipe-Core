using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>Streams JSON files (array or NDJSON) as pipeline source.</summary>
/// <typeparam name="T">Item type to deserialize.</typeparam>
public class JsonFileSource<T> : ISource<T>
{
    private readonly string _path;
    private readonly Func<Stream, CancellationToken, ValueTask<List<T>?>> _deserializeListAsync;
    private readonly Func<string, T?> _deserializeItem;

    /// <summary>Create source for given JSON file path.</summary>
    /// <param name="path">Path to JSON file (array or NDJSON).</param>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    /// <exception cref="ArgumentException">Thrown when path is empty or whitespace.</exception>
    [RequiresUnreferencedCode("JsonSerializerOptions-based JSON file reading may require reflection metadata. Use the JsonTypeInfo constructor for trimming and NativeAOT.")]
    [RequiresDynamicCode("JsonSerializerOptions-based JSON file reading may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
    public JsonFileSource(string path)
    {
        if (path == null)
            throw new ArgumentNullException(nameof(path));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty or whitespace.", nameof(path));
        _path = path;
        _deserializeListAsync = static (stream, token) =>
            JsonSerializer.DeserializeAsync<List<T>>(stream, cancellationToken: token);
        _deserializeItem = static line => JsonSerializer.Deserialize<T>(line);
    }

    /// <summary>Create source for given JSON file path using source-generated JSON metadata.</summary>
    /// <param name="path">Path to JSON file (array or NDJSON).</param>
    /// <param name="listTypeInfo">Source-generated type information for JSON array files.</param>
    /// <param name="itemTypeInfo">Source-generated type information for NDJSON items.</param>
    /// <exception cref="ArgumentNullException">Thrown when path or type information is null.</exception>
    /// <exception cref="ArgumentException">Thrown when path is empty or whitespace.</exception>
    public JsonFileSource(
        string path,
        JsonTypeInfo<List<T>> listTypeInfo,
        JsonTypeInfo<T> itemTypeInfo)
    {
        if (path == null)
            throw new ArgumentNullException(nameof(path));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty or whitespace.", nameof(path));
        ArgumentNullException.ThrowIfNull(listTypeInfo);
        ArgumentNullException.ThrowIfNull(itemTypeInfo);

        _path = path;
        _deserializeListAsync = (stream, token) =>
            JsonSerializer.DeserializeAsync(stream, listTypeInfo, token);
        _deserializeItem = line => JsonSerializer.Deserialize(line, itemTypeInfo);
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync(
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        char firstChar;
        using (var peekReader = new StreamReader(_path))
        {
            firstChar = (char)peekReader.Read();
        }

        if (firstChar == '[')
        {
            using var stream = File.OpenRead(_path);
            var items = await _deserializeListAsync(stream, ct).ConfigureAwait(false);
            if (items != null)
                foreach (var item in items)
                    if (item != null)
                        yield return new ProcessingContext<T>(item);
        }
        else
        {
            using var reader = new StreamReader(_path);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var item = _deserializeItem(line);
                if (item != null)
                    yield return new ProcessingContext<T>(item);
            }
        }
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;
}
