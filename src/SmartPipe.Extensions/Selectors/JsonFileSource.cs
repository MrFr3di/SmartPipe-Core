using System.Runtime.CompilerServices;
using System.Text.Json;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>Streams JSON files (array or NDJSON) as pipeline source.</summary>
public class JsonFileSource<T> : ISource<T>
{
    private readonly string _path;

    public JsonFileSource(string path) => _path = path;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var firstChar = (char)new StreamReader(_path).Read();
        if (firstChar == '[')
        {
            using var stream = File.OpenRead(_path);
            var items = await JsonSerializer.DeserializeAsync<List<T>>(stream, cancellationToken: ct).ConfigureAwait(false);
            if (items != null)
                foreach (var item in items)
                    yield return new ProcessingContext<T>(item);
        }
        else
        {
            using var reader = new StreamReader(_path);
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var item = JsonSerializer.Deserialize<T>(line);
                if (item != null) yield return new ProcessingContext<T>(item);
            }
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
