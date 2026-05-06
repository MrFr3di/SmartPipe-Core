using System.Text.Json;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Writes pipeline output to a JSON file.</summary>
/// <typeparam name="T">Item type.</typeparam>
public class JsonFileSink<T> : ISink<T>
{
    private readonly string _path;
    private readonly List<T> _buffer = new();

    /// <summary>Create JSON file sink for given path.</summary>
    /// <param name="path">Output file path.</param>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    public JsonFileSink(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (result.IsSuccess && result.Value != null)
            lock (_buffer) _buffer.Add(result.Value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        var json = JsonSerializer.Serialize(_buffer, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_path, json);
    }
}
