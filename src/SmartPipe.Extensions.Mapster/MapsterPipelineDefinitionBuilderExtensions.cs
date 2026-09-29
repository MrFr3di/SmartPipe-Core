#nullable enable

using System.Diagnostics.CodeAnalysis;
using Mapster;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Mapster;

/// <summary>Attaches Mapster mapping stages to typed pipeline definitions.</summary>
public static class MapsterPipelineDefinitionBuilderExtensions
{
    /// <summary>Appends a Mapster mapping stage and returns the typed definition builder.</summary>
    /// <typeparam name="TInput">The pipeline input type, which the returned builder preserves.</typeparam>
    /// <typeparam name="TOutput">The mapped stage output type.</typeparam>
    /// <param name="builder">The initial typed definition builder.</param>
    /// <param name="stageKey">The stage key passed to Core's <c>Transform</c>.</param>
    /// <param name="configure">Optional composition-time configuration callback.</param>
    /// <returns>The typed definition builder whose input type is unchanged.</returns>
    [RequiresUnreferencedCode(MapsterPipelineComponents.ReflectionMessage)]
    [RequiresDynamicCode(MapsterPipelineComponents.DynamicMessage)]
    public static PipelineDefinitionBuilder<TInput, TOutput> MapWithMapster<TInput, TOutput>(
        this PipelineDefinitionBuilder<TInput> builder,
        PipelineStageKey stageKey,
        Action<TypeAdapterConfig>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Transform(stageKey, MapsterPipelineComponents.Transform<TInput, TOutput>(configure));
    }

    /// <summary>Appends a Mapster mapping stage after an existing stage.</summary>
    /// <typeparam name="TPipelineInput">The original pipeline input type, which the returned builder preserves.</typeparam>
    /// <typeparam name="TCurrent">The current stage output type that Mapster maps from.</typeparam>
    /// <typeparam name="TOutput">The mapped stage output type.</typeparam>
    /// <param name="builder">The typed definition builder produced by a previous stage.</param>
    /// <param name="stageKey">The stage key passed to Core's <c>Transform</c>.</param>
    /// <param name="configure">Optional composition-time configuration callback.</param>
    /// <returns>The typed definition builder whose input type is the original pipeline input.</returns>
    [RequiresUnreferencedCode(MapsterPipelineComponents.ReflectionMessage)]
    [RequiresDynamicCode(MapsterPipelineComponents.DynamicMessage)]
    public static PipelineDefinitionBuilder<TPipelineInput, TOutput> MapWithMapster<TPipelineInput, TCurrent, TOutput>(
        this PipelineDefinitionBuilder<TPipelineInput, TCurrent> builder,
        PipelineStageKey stageKey,
        Action<TypeAdapterConfig>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Transform(stageKey, MapsterPipelineComponents.Transform<TCurrent, TOutput>(configure));
    }
}
