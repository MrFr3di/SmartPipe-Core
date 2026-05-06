using System.Runtime.CompilerServices;
using System.Text.Json;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>Streams JSON files (array or NDJSON) as pipeline source.</summary>
/// <typeparam name="T">Item type to deserialize.</typeparam>
public class JsonFileSource<T> : ISource<T>
{
    private readonly string _path;

    /// <summary>Create source for given JSON file path.</summary>
    /// <param name="path">Path to JSON file (array or NDJSON).</param>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    /// <exception cref="ArgumentException">Thrown when path is empty or whitespace.</exception>
    public JsonFileSource(string path)
    {
        if (path == null)
            throw new ArgumentNullException(nameof(path));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty or whitespace.", nameof(path));
        _path = path;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        char firstChar;
        using (var peekReader = new StreamReader(_path))
        {
            firstChar = (char)peekReader.Read();
        }

        if (firstChar == '[')
        {
            using var stream = File.OpenRead(_path);
            var items = await JsonSerializer.DeserializeAsync<List<T>>(stream, cancellationToken: ct).ConfigureAwait(false);
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
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var item = JsonSerializer.Deserialize<T>(line);
                if (item != null) yield return new ProcessingContext<T>(item);
            }
        }
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;
}
