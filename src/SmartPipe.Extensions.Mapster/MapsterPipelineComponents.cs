#nullable enable

using System.Diagnostics.CodeAnalysis;
using Mapster;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Mapster;

/// <summary>Creates runtime-owned Mapster mapping components.</summary>
/// <remarks>
/// Composing a component fixes the whole configuration pipeline: one caller callback runs against a
/// fresh working <see cref="TypeAdapterConfig"/>, that configuration is cloned once into a private
/// container, and the requested root pair is compiled once. The captured delegate is then reused by a
/// fresh Core-owned transformer per run, so no run clones or compiles the root pair again.
/// </remarks>
public static class MapsterPipelineComponents
{
    internal const string ReflectionMessage =
        "Mapster runtime mapping uses reflection metadata.";

    internal const string DynamicMessage =
        "Mapster runtime mapping compiles expressions at runtime.";

    /// <summary>Creates a Mapster mapping transform for the requested pair.</summary>
    /// <typeparam name="TInput">The source type to map from.</typeparam>
    /// <typeparam name="TOutput">The destination type to map to.</typeparam>
    /// <param name="configure">
    /// Optional callback invoked exactly once during composition against a fresh working
    /// configuration. An exception thrown here is a composition failure.
    /// </param>
    /// <returns>A runtime-owned transformer descriptor over the captured mapping delegate.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicMessage)]
    public static PipelineComponent<IPipelineTransformer<TInput, TOutput>> Transform<TInput, TOutput>(
        Action<TypeAdapterConfig>? configure = null)
    {
        var working = new TypeAdapterConfig();
        configure?.Invoke(working);

        var privateConfig = working.Clone();
        var map = privateConfig.GetMapFunction<TInput, TOutput>();

        return PipelineComponent.RuntimeOwned<IPipelineTransformer<TInput, TOutput>>(
            (context, cancellationToken) => ValueTask.FromResult(
                PipelineTransformer.FromFunc<TInput, TOutput>(
                    (input, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        var output = map(input);
                        token.ThrowIfCancellationRequested();
                        return ValueTask.FromResult(output);
                    })));
    }
}
