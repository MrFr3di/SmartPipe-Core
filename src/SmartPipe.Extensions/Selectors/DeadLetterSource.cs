using System.Text.Json;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>Reads failed items from DeadLetterSink JSON for reprocessing.</summary>
public class DeadLetterSource<T> : ISource<T>
{
    private readonly string _path;

    public DeadLetterSource(string path) => _path = path;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            throw new FileNotFoundException($"Dead letter file not found: {_path}");
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync(CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(_path, ct);
        var results = JsonSerializer.Deserialize<List<ProcessingResult<T>>>(json);
        if (results != null)
            foreach (var result in results)
                if (!result.IsSuccess && result.Value != null)
                    yield return new ProcessingContext<T>(result.Value);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
