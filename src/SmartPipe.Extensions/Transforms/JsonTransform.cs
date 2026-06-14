using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// JSON serialization/deserialization transformer using System.Text.Json.
/// Serializes <typeparamref name="TInput"/> to JSON, then deserializes to <typeparamref name="TOutput"/>.
/// Supports <see cref="JsonSerializerOptions"/> configuration for custom serialization behavior.
/// Implements <see cref="IPipelineTransformer{TInput, TOutput}"/> for pipeline integration.
/// </summary>
/// <typeparam name="TInput">The input type to serialize.</typeparam>
/// <typeparam name="TOutput">The output type to deserialize.</typeparam>
public class JsonTransform<TInput, TOutput> : IPipelineTransformer<TInput, TOutput>
{
    private readonly Func<TInput, string> _serialize;
    private readonly Func<string, TOutput?> _deserialize;

    /// <summary>
    /// Initializes a new instance of <see cref="JsonTransform{TInput, TOutput}"/> with default JSON serializer options.
    /// </summary>
    [RequiresUnreferencedCode("JsonSerializerOptions-based serialization may require reflection metadata. Use the JsonTypeInfo constructor for trimming and NativeAOT.")]
    [RequiresDynamicCode("JsonSerializerOptions-based serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
    public JsonTransform()
        : this(options: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="JsonTransform{TInput, TOutput}"/>.
    /// </summary>
    /// <param name="options">Optional JSON serializer options. If null, default options with case-insensitive property matching are used.</param>
    [RequiresUnreferencedCode("JsonSerializerOptions-based serialization may require reflection metadata. Use the JsonTypeInfo constructor for trimming and NativeAOT.")]
    [RequiresDynamicCode("JsonSerializerOptions-based serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
#pragma warning disable RS0027 // Existing 1.x optional constructor preserved for source compatibility.
    public JsonTransform(JsonSerializerOptions? options = null)
    {
        var serializerOptions =
            options
            ?? new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false,
            };
        _serialize = value => JsonSerializer.Serialize(value, serializerOptions);
        _deserialize = json => JsonSerializer.Deserialize<TOutput>(json, serializerOptions);
    }
#pragma warning restore RS0027

    /// <summary>
    /// Initializes a new instance of <see cref="JsonTransform{TInput, TOutput}"/> using source-generated JSON metadata.
    /// </summary>
    /// <param name="inputTypeInfo">Source-generated type information for the input type.</param>
    /// <param name="outputTypeInfo">Source-generated type information for the output type.</param>
    /// <exception cref="ArgumentNullException">Thrown when either type information argument is null.</exception>
    public JsonTransform(JsonTypeInfo<TInput> inputTypeInfo, JsonTypeInfo<TOutput> outputTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(inputTypeInfo);
        ArgumentNullException.ThrowIfNull(outputTypeInfo);
        _serialize = value => JsonSerializer.Serialize(value, inputTypeInfo);
        _deserialize = json => JsonSerializer.Deserialize(json, outputTypeInfo);
    }

    /// <inheritdoc/>
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default
    )
    {
        try
        {
            var json = _serialize(envelope.Payload);
            var result = _deserialize(json);

            return ValueTask.FromResult(StageResult<TOutput>.Success(result!));
        }
        catch (JsonException ex)
        {
            return ValueTask.FromResult(
                StageResult<TOutput>.Failure(
                    new SmartPipeError(
                        $"JSON transform failed: {ex.Message}",
                        ErrorType.Permanent,
                        "Serialization",
                        ex
                    )
                )
            );
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
