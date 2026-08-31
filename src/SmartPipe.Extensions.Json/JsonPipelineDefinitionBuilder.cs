#nullable enable

using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Json;

/// <summary>Starts typed pipeline definitions backed by JSON file components.</summary>
public static class JsonPipelineDefinitionBuilder
{
    /// <summary>Starts a typed definition with a JSON file source.</summary>
    public static PipelineDefinitionBuilder<T> FromJsonFile<T>(
        PipelineKey pipelineKey,
        string path,
        JsonTypeInfo<T> itemTypeInfo,
        JsonTypeInfo<List<T>> batchTypeInfo,
        JsonFileSourceOptions options,
        ILoggerFactory? loggerFactory = null) =>
        SmartPipe.Core.PipelineDefinitionBuilder.From(
            pipelineKey,
            JsonPipelineComponents.FileSource(
                path,
                itemTypeInfo,
                batchTypeInfo,
                options,
                loggerFactory));

    /// <summary>Starts a typed definition with a dead-letter JSON source.</summary>
    public static PipelineDefinitionBuilder<T> FromJsonDeadLetterFile<T>(
        PipelineKey pipelineKey,
        string path,
        JsonTypeInfo<DeadLetterEnvelope<T>> envelopeTypeInfo,
        DeadLetterSourceOptions options,
        ILoggerFactory? loggerFactory = null) =>
        SmartPipe.Core.PipelineDefinitionBuilder.From(
            pipelineKey,
            JsonPipelineComponents.DeadLetterSource(
                path,
                envelopeTypeInfo,
                options,
                loggerFactory));
}

/// <summary>Adds JSON transforms and file sinks to typed definitions.</summary>
public static class JsonPipelineDefinitionBuilderExtensions
{
#pragma warning disable RS0026 // The canonical JSON builder intentionally mirrors Core's typed overload families.
    /// <summary>Appends a JSON transform to a source-only definition.</summary>
    public static PipelineDefinitionBuilder<TInput, TNext> TransformJson<TInput, TNext>(
        this PipelineDefinitionBuilder<TInput> builder,
        PipelineStageKey stageKey,
        JsonTypeInfo<TInput> inputTypeInfo,
        JsonTypeInfo<TNext> outputTypeInfo,
        StageFailureOptions? failureOptions = null,
        StageDeadLetterOptions<TInput>? deadLetterOptions = null,
        string? stageName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Transform(
            stageKey,
            JsonPipelineComponents.Transform(inputTypeInfo, outputTypeInfo),
            failureOptions,
            deadLetterOptions,
            stageName);
    }

    /// <summary>Appends a JSON transform to a multi-stage definition.</summary>
    public static PipelineDefinitionBuilder<TInput, TNext> TransformJson<TInput, TCurrent, TNext>(
        this PipelineDefinitionBuilder<TInput, TCurrent> builder,
        PipelineStageKey stageKey,
        JsonTypeInfo<TCurrent> inputTypeInfo,
        JsonTypeInfo<TNext> outputTypeInfo,
        StageFailureOptions? failureOptions = null,
        StageDeadLetterOptions<TCurrent>? deadLetterOptions = null,
        string? stageName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Transform(
            stageKey,
            JsonPipelineComponents.Transform(inputTypeInfo, outputTypeInfo),
            failureOptions,
            deadLetterOptions,
            stageName);
    }

    /// <summary>Completes a source-only definition with a JSON file sink.</summary>
    public static PipelineDefinition<T, T> ToJsonFile<T>(
        this PipelineDefinitionBuilder<T> builder,
        string path,
        JsonTypeInfo<T> itemTypeInfo,
        JsonTypeInfo<List<T>> batchTypeInfo,
        JsonFileSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(JsonPipelineComponents.FileSink(path, itemTypeInfo, batchTypeInfo, options));
    }

    /// <summary>Completes a multi-stage definition with a JSON file sink.</summary>
    public static PipelineDefinition<TInput, TOutput> ToJsonFile<TInput, TOutput>(
        this PipelineDefinitionBuilder<TInput, TOutput> builder,
        string path,
        JsonTypeInfo<TOutput> itemTypeInfo,
        JsonTypeInfo<List<TOutput>> batchTypeInfo,
        JsonFileSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(JsonPipelineComponents.FileSink(path, itemTypeInfo, batchTypeInfo, options));
    }
#pragma warning restore RS0026
}
