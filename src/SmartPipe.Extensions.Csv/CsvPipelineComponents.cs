using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;
using SmartPipe.Extensions.Csv.Internal;

namespace SmartPipe.Extensions.Csv;

/// <summary>Creates runtime-owned strict CSV pipeline components.</summary>
public static class CsvPipelineComponents
{
    /// <summary>Creates a lazy, per-run strict CSV file source.</summary>
    [RequiresUnreferencedCode("CsvHelper object mapping uses reflection.")]
    [RequiresDynamicCode("CsvHelper object mapping compiles delegates at runtime.")]
    public static PipelineComponent<IPipelineSource<T>> FileSource<T>(
        string path,
        CsvSourceOptions options,
        CsvMapRegistration<T>? map = null,
        ILoggerFactory? loggerFactory = null)
    {
        var validatedPath = ValidatePath(path);
        var snapshot = CsvSourceOptionsSnapshot.Create(options, loggerFactory is not null);
        var registration = map ?? CsvMapRegistration<T>.Auto;

        return PipelineComponent.RuntimeOwned<IPipelineSource<T>>(
            (_, cancellationToken) => ValueTask.FromResult<IPipelineSource<T>>(
                new StrictCsvFileSource<T>(
                    validatedPath,
                    snapshot,
                    registration,
                    loggerFactory?.CreateLogger<StrictCsvFileSource<T>>(),
                    cancellationToken)));
    }

    /// <summary>Creates a lazy, per-run strict CSV file sink.</summary>
    [RequiresUnreferencedCode("CsvHelper object mapping uses reflection.")]
    [RequiresDynamicCode("CsvHelper object mapping compiles delegates at runtime.")]
    public static PipelineComponent<IPipelineSink<T>> FileSink<T>(
        string path,
        CsvSinkOptions options,
        CsvMapRegistration<T>? map = null)
    {
        var validatedPath = ValidatePath(path);
        var snapshot = CsvSinkOptionsSnapshot.Create(options);
        var registration = map ?? CsvMapRegistration<T>.Auto;

        return PipelineComponent.RuntimeOwned<IPipelineSink<T>>(
            (_, cancellationToken) => ValueTask.FromResult<IPipelineSink<T>>(
                new StrictCsvFileSink<T>(
                    validatedPath,
                    snapshot,
                    registration,
                    cancellationToken)));
    }

    private static string ValidatePath(string? path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty or whitespace.", nameof(path));
        return path;
    }
}
