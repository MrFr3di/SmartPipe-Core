using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Npgsql;
using SmartPipe.Core;

namespace SmartPipe.Extensions.PostgreSql;

/// <summary>Completes typed definitions with a binary <c>COPY … FROM STDIN (FORMAT BINARY)</c> batch sink.</summary>
/// <remarks>
/// The sink accepts one complete COPY batch per write. Applications must supply already-formed batches; this package
/// deliberately does not introduce a generic batching or windowing runtime.
/// </remarks>
[SuppressMessage(
    "ApiDesign",
    "RS0026",
    Justification = "The source-only and multi-stage forms are distinguished by their generic arity and builder type; their optional tail is intentionally identical.")]
public static class PostgreSqlPipelineDefinitionBuilderExtensions
{
    /// <summary>Completes a source-only batch definition with a PostgreSQL binary COPY sink.</summary>
    /// <typeparam name="T">The batch row type.</typeparam>
    /// <param name="builder">The batch definition builder.</param>
    /// <param name="dataSource">The application-owned, long-lived data source. Never disposed by SmartPipe.</param>
    /// <param name="copyFromCommand">The complete configuration COPY statement sent to PostgreSQL.</param>
    /// <param name="rowWriter">Writes exactly one row into the importer.</param>
    /// <param name="options">The COPY sink options.</param>
    /// <param name="loggerFactory">An optional borrowed logger factory. Never disposed by SmartPipe.</param>
    /// <returns>A completed pipeline definition.</returns>
    public static PipelineDefinition<IReadOnlyList<T>, IReadOnlyList<T>> ToPostgreSqlBinaryCopy<T>(
        this PipelineDefinitionBuilder<IReadOnlyList<T>> builder,
        NpgsqlDataSource dataSource,
        string copyFromCommand,
        Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> rowWriter,
        PostgreSqlBinaryCopySinkOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(
            PostgreSqlPipelineComponents.BinaryCopyBatchSink(dataSource, copyFromCommand, rowWriter, options, loggerFactory));
    }

    /// <summary>Completes a multi-stage batch definition with a PostgreSQL binary COPY sink.</summary>
    /// <typeparam name="TInput">The upstream payload type.</typeparam>
    /// <typeparam name="T">The batch row type.</typeparam>
    /// <param name="builder">The multi-stage definition builder.</param>
    /// <param name="dataSource">The application-owned, long-lived data source. Never disposed by SmartPipe.</param>
    /// <param name="copyFromCommand">The complete configuration COPY statement sent to PostgreSQL.</param>
    /// <param name="rowWriter">Writes exactly one row into the importer.</param>
    /// <param name="options">The COPY sink options.</param>
    /// <param name="loggerFactory">An optional borrowed logger factory. Never disposed by SmartPipe.</param>
    /// <returns>A completed pipeline definition.</returns>
    public static PipelineDefinition<TInput, IReadOnlyList<T>> ToPostgreSqlBinaryCopy<TInput, T>(
        this PipelineDefinitionBuilder<TInput, IReadOnlyList<T>> builder,
        NpgsqlDataSource dataSource,
        string copyFromCommand,
        Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> rowWriter,
        PostgreSqlBinaryCopySinkOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(
            PostgreSqlPipelineComponents.BinaryCopyBatchSink(dataSource, copyFromCommand, rowWriter, options, loggerFactory));
    }
}
