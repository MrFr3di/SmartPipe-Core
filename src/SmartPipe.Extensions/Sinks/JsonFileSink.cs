using System.Text.Json;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Writes pipeline output to a JSON file.</summary>
public class JsonFileSink<T> : ISink<T>
{
    private readonly string _path;
    private readonly List<T> _buffer = new();

    public JsonFileSink(string path) => _path = path;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (result.IsSuccess && result.Value != null)
            lock (_buffer) _buffer.Add(result.Value);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var json = JsonSerializer.Serialize(_buffer, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_path, json);
    }
}
