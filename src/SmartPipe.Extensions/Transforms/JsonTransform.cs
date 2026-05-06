using System.Text.Json;
using System.Text.Json.Serialization;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// JSON serialization/deserialization transformer using System.Text.Json.
/// Serializes <typeparamref name="TInput"/> to JSON, then deserializes to <typeparamref name="TOutput"/>.
/// Supports <see cref="JsonSerializerOptions"/> configuration for custom serialization behavior.
/// Implements <see cref="ITransformer{TInput, TOutput}"/> for pipeline integration.
/// </summary>
/// <typeparam name="TInput">The input type to serialize.</typeparam>
/// <typeparam name="TOutput">The output type to deserialize.</typeparam>
public class JsonTransform<TInput, TOutput> : ITransformer<TInput, TOutput>
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Initializes a new instance of <see cref="JsonTransform{TInput, TOutput}"/>.
    /// </summary>
    /// <param name="options">Optional JSON serializer options. If null, default options with case-insensitive property matching are used.</param>
    public JsonTransform(JsonSerializerOptions? options = null)
    {
        _options = options ?? new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;
}
