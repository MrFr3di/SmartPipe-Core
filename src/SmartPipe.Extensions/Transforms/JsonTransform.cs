using System.Text.Json;
using System.Text.Json.Serialization;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// JSON serialization/deserialization transformer using System.Text.Json source generation.
/// Supports AOT compilation and zero-reflection serialization.
/// </summary>
/// <typeparam name="TInput">Input type to serialize.</typeparam>
/// <typeparam name="TOutput">Output type to deserialize.</typeparam>
public class JsonTransform<TInput, TOutput> : ITransformer<TInput, TOutput>
{
    private readonly JsonSerializerOptions _options;

    public JsonTransform(JsonSerializerOptions? options = null)
    {
        _options = options ?? new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask<ProcessingResult<TOutput>> TransformAsync(ProcessingContext<TInput> ctx, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(ctx.Payload, _options);
            var result = JsonSerializer.Deserialize<TOutput>(json, _options);

            return ValueTask.FromResult(
                ProcessingResult<TOutput>.Success(result!, ctx.TraceId));
        }
        catch (JsonException ex)
        {
            return ValueTask.FromResult(
                ProcessingResult<TOutput>.Failure(
                    new SmartPipeError($"JSON transform failed: {ex.Message}", ErrorType.Permanent, "Serialization", ex),
                    ctx.TraceId));
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
