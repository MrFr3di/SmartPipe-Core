using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>Reads failed items from DeadLetterSink JSON for reprocessing.</summary>
/// <typeparam name="T">Item type.</typeparam>
public class DeadLetterSource<T> : ISource<T>
{
    private readonly string _path;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Create source for given dead letter JSON file.</summary>
    /// <param name="path">Path to dead letter JSON file.</param>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    public DeadLetterSource(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            throw new FileNotFoundException($"Dead letter file not found: {_path}");
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(_path, ct);
        
        // Try to deserialize as JsonElement first for flexible handling
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        // Handle both JSON arrays and single objects
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                var context = ProcessElement(element);
                if (context != null)
                    yield return context;
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            // Single object - process it directly
            var context = ProcessElement(root);
            if (context != null)
                yield return context;
        }
        else
        {
            throw new JsonException($"Unexpected JSON root element type: {root.ValueKind}");
        }
    }

    private static ProcessingContext<T>? ProcessElement(JsonElement element)
    {
        if (!element.TryGetProperty("IsSuccess", out var isSuccessProp) || 
            !isSuccessProp.GetBoolean())
        {
            // Only process failed items
            if (element.TryGetProperty("Value", out var valueProp))
            {
                var value = valueProp.Deserialize<T>(_jsonOptions);
                if (value != null)
                    return new ProcessingContext<T>(value);
            }
        }
        return null;
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;
}
