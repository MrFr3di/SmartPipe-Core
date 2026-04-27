using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>
/// Sink that captures failed items for later analysis.
/// Saves only errors (IsSuccess = false) to a file in JSON format.
/// </summary>
public class DeadLetterSink<T> : ISink<T>
{
    private readonly string _path;
    private readonly List<ProcessingResult<T>> _dead = new();

    public DeadLetterSink(string path = "dead_letter.json") => _path = path;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (!result.IsSuccess)
            lock (_dead) _dead.Add(result);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(_dead, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_path, json);
    }
}
