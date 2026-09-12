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
