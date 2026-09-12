using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Csv;

/// <summary>Starts typed pipeline definitions backed by strict CSV files.</summary>
public static class CsvPipelineDefinitionBuilder
{
    /// <summary>Starts a typed definition with a strict CSV file source.</summary>
    [RequiresUnreferencedCode("CsvHelper object mapping uses reflection.")]
    [RequiresDynamicCode("CsvHelper object mapping compiles delegates at runtime.")]
    public static PipelineDefinitionBuilder<T> FromCsvFile<T>(
        PipelineKey pipelineKey,
        string path,
        CsvSourceOptions options,
        CsvMapRegistration<T>? map = null,
        ILoggerFactory? loggerFactory = null) =>
        SmartPipe.Core.PipelineDefinitionBuilder.From(
            pipelineKey,
            CsvPipelineComponents.FileSource(path, options, map, loggerFactory));
}

/// <summary>Adds strict CSV file sinks to typed definitions.</summary>
public static class CsvPipelineDefinitionBuilderExtensions
{
    /// <summary>Completes a source-only definition with a strict CSV file sink.</summary>
    [RequiresUnreferencedCode("CsvHelper object mapping uses reflection.")]
    [RequiresDynamicCode("CsvHelper object mapping compiles delegates at runtime.")]
    public static PipelineDefinition<T, T> ToCsvFile<T>(
        this PipelineDefinitionBuilder<T> builder,
        string path,
        CsvSinkOptions options,
        CsvMapRegistration<T>? map = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(CsvPipelineComponents.FileSink(path, options, map));
    }

    /// <summary>Completes a multi-stage definition with a strict CSV file sink.</summary>
    [RequiresUnreferencedCode("CsvHelper object mapping uses reflection.")]
    [RequiresDynamicCode("CsvHelper object mapping compiles delegates at runtime.")]
    public static PipelineDefinition<TInput, TOutput> ToCsvFile<TInput, TOutput>(
        this PipelineDefinitionBuilder<TInput, TOutput> builder,
        string path,
        CsvSinkOptions options,
        CsvMapRegistration<TOutput>? map = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(CsvPipelineComponents.FileSink(path, options, map));
    }
}
