#nullable enable

using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Json;

/// <summary>Creates runtime-owned JSON pipeline components.</summary>
public static class JsonPipelineComponents
{
    /// <summary>Creates a lazy, per-run JSON file source component.</summary>
    public static PipelineComponent<IPipelineSource<T>> FileSource<T>(
        string path,
        JsonTypeInfo<T> itemTypeInfo,
        JsonTypeInfo<List<T>> batchTypeInfo,
        JsonFileSourceOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        var validatedPath = ValidatePath(path);
        var validatedOptions = JsonInputOptionsValidator.Validate(options, loggerFactory is not null);
        var metadata = JsonMetadataSnapshot.ForFile(itemTypeInfo, batchTypeInfo, validatedOptions.MaxDepth);

        return PipelineComponent.RuntimeOwned<IPipelineSource<T>>(
            (_, _) => ValueTask.FromResult<IPipelineSource<T>>(
                new JsonFileSource<T>(
                    validatedPath,
                    metadata.Item,
                    metadata.Batch,
                    validatedOptions,
                    loggerFactory?.CreateLogger<JsonFileSource<T>>())));
    }

    /// <summary>Creates a lazy, per-run JSON file sink component.</summary>
    public static PipelineComponent<IPipelineSink<T>> FileSink<T>(
        string path,
        JsonTypeInfo<T> itemTypeInfo,
        JsonTypeInfo<List<T>> batchTypeInfo,
        JsonFileSinkOptions options)
    {
        var validatedPath = ValidatePath(path);
        var validatedOptions = JsonInputOptionsValidator.Validate(options);
        var metadata = JsonMetadataSnapshot.ForFile(itemTypeInfo, batchTypeInfo);

        return PipelineComponent.RuntimeOwned<IPipelineSink<T>>(
            (_, _) => ValueTask.FromResult<IPipelineSink<T>>(
                new JsonFileSink<T>(
                    validatedPath,
                    metadata.Item,
                    metadata.Batch,
                    validatedOptions)));
    }

    /// <summary>Creates a lazy, per-run JSON transform component.</summary>
    public static PipelineComponent<IPipelineTransformer<TInput, TOutput>> Transform<TInput, TOutput>(
        JsonTypeInfo<TInput> inputTypeInfo,
        JsonTypeInfo<TOutput> outputTypeInfo)
    {
        var inputMetadata = JsonMetadataSnapshot.ForValue(inputTypeInfo);
        var outputMetadata = JsonMetadataSnapshot.ForValue(outputTypeInfo);

        return PipelineComponent.RuntimeOwned<IPipelineTransformer<TInput, TOutput>>(
            (_, _) => ValueTask.FromResult<IPipelineTransformer<TInput, TOutput>>(
                new JsonTransform<TInput, TOutput>(inputMetadata, outputMetadata)));
    }

    /// <summary>Creates a lazy, per-run dead-letter JSON source component.</summary>
    public static PipelineComponent<IPipelineSource<T>> DeadLetterSource<T>(
        string path,
        JsonTypeInfo<DeadLetterEnvelope<T>> envelopeTypeInfo,
        DeadLetterSourceOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        var validatedPath = ValidatePath(path);
        var validatedOptions = JsonInputOptionsValidator.Validate(options, loggerFactory is not null);
        var metadata = JsonMetadataSnapshot.ForDeadLetterEnvelope(
            envelopeTypeInfo,
            validatedOptions.MaxDepth);

        return PipelineComponent.RuntimeOwned<IPipelineSource<T>>(
            (_, _) => ValueTask.FromResult<IPipelineSource<T>>(
                loggerFactory is null
                    ? new DeadLetterSource<T>(validatedPath, metadata, validatedOptions)
                    : new DeadLetterSource<T>(
                        validatedPath,
                        metadata,
                        validatedOptions,
                        loggerFactory.CreateLogger<DeadLetterSource<T>>())));
    }

    /// <summary>Creates a lazy, per-run dead-letter JSON sink component.</summary>
    public static PipelineComponent<IPipelineSink<DeadLetterEnvelope<T>>> DeadLetterSink<T>(
        string path,
        JsonTypeInfo<DeadLetterEnvelope<T>> envelopeTypeInfo,
        DeadLetterSinkOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        var validatedPath = ValidatePath(path);
        var validatedOptions = JsonInputOptionsValidator.Validate(options, loggerFactory is not null);
        var metadata = JsonMetadataSnapshot.ForDeadLetterEnvelope(envelopeTypeInfo);

        return PipelineComponent.RuntimeOwned<IPipelineSink<DeadLetterEnvelope<T>>>(
            (_, _) => ValueTask.FromResult<IPipelineSink<DeadLetterEnvelope<T>>>(
                new DeadLetterSink<T>(
                    validatedPath,
                    new JsonLinesDeadLetterSerializer<T>(metadata),
                    validatedOptions,
                    loggerFactory?.CreateLogger<DeadLetterSink<T>>(),
                    stream: null)));
    }

    private static string ValidatePath(string? path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty or whitespace.", nameof(path));
        return path;
    }
}
