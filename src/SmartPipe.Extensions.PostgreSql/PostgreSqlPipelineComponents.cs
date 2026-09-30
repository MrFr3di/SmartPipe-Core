using Microsoft.Extensions.Logging;
using Npgsql;
using SmartPipe.Core;
using SmartPipe.Extensions.PostgreSql.Internal;

namespace SmartPipe.Extensions.PostgreSql;

/// <summary>Creates runtime-owned PostgreSQL-native pipeline components.</summary>
/// <remarks>
/// Every factory is pure: it validates arguments, snapshots options and returns a lazy descriptor. No connection is
/// opened, no COPY or LISTEN protocol work starts and the application-owned <see cref="NpgsqlDataSource"/> is never
/// mutated or disposed. Each pipeline run obtains its own connection.
/// </remarks>
public static class PostgreSqlPipelineComponents
{
    /// <summary>Creates a lazy, per-run binary <c>COPY … TO STDOUT (FORMAT BINARY)</c> source.</summary>
    /// <typeparam name="T">The row value produced by <paramref name="rowReader"/>.</typeparam>
    /// <param name="dataSource">The application-owned, long-lived data source. Never disposed by SmartPipe.</param>
    /// <param name="copyToCommand">The complete configuration COPY statement sent to PostgreSQL.</param>
    /// <param name="rowReader">
    /// Reads exactly one row from the exporter. The callback borrows the exporter until its returned value task
    /// completes, must consume or skip every column before returning, and must not dispose, retain or concurrently
    /// use the cursor.
    /// </param>
    /// <param name="options">The COPY source options.</param>
    /// <param name="loggerFactory">An optional borrowed logger factory. Never disposed by SmartPipe.</param>
    /// <returns>A runtime-owned source descriptor.</returns>
    public static PipelineComponent<IPipelineSource<T>> BinaryCopySource<T>(
        NpgsqlDataSource dataSource,
        string copyToCommand,
        Func<NpgsqlBinaryExporter, int, CancellationToken, ValueTask<T>> rowReader,
        PostgreSqlBinaryCopySourceOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        var validatedDataSource = PostgreSqlArguments.NonMultiplexingDataSource(dataSource, PostgreSqlErrorMessages.CopyMultiplexingUnsupported);
        var validatedCommand = PostgreSqlArguments.CopyCommand(copyToCommand, nameof(copyToCommand));
        ArgumentNullException.ThrowIfNull(rowReader, nameof(rowReader));
        var snapshot = PostgreSqlBinaryCopySourceOptionsSnapshot.Create(options);

        return PipelineComponent.RuntimeOwned<IPipelineSource<T>>(
            (_, cancellationToken) => ValueTask.FromResult<IPipelineSource<T>>(
                new PostgreSqlBinaryCopySource<T>(
                    validatedDataSource,
                    validatedCommand,
                    rowReader,
                    snapshot,
                    loggerFactory?.CreateLogger<PostgreSqlBinaryCopySource<T>>(),
                    cancellationToken)));
    }

    /// <summary>Creates a lazy, per-run binary <c>COPY … FROM STDIN (FORMAT BINARY)</c> batch sink.</summary>
    /// <typeparam name="T">The row value written by <paramref name="rowWriter"/>.</typeparam>
    /// <param name="dataSource">The application-owned, long-lived data source. Never disposed by SmartPipe.</param>
    /// <param name="copyFromCommand">The complete configuration COPY statement sent to PostgreSQL.</param>
    /// <param name="rowWriter">
    /// Writes exactly one row into the importer. The callback borrows the importer until its returned value task
    /// completes and must not dispose, retain or concurrently use the cursor.
    /// </param>
    /// <param name="options">The COPY sink options.</param>
    /// <param name="loggerFactory">An optional borrowed logger factory. Never disposed by SmartPipe.</param>
    /// <returns>A runtime-owned sink descriptor accepting one complete COPY batch per write.</returns>
    public static PipelineComponent<IPipelineSink<IReadOnlyList<T>>> BinaryCopyBatchSink<T>(
        NpgsqlDataSource dataSource,
        string copyFromCommand,
        Func<NpgsqlBinaryImporter, T, CancellationToken, ValueTask> rowWriter,
        PostgreSqlBinaryCopySinkOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        var validatedDataSource = PostgreSqlArguments.NonMultiplexingDataSource(dataSource, PostgreSqlErrorMessages.CopyMultiplexingUnsupported);
        var validatedCommand = PostgreSqlArguments.CopyCommand(copyFromCommand, nameof(copyFromCommand));
        ArgumentNullException.ThrowIfNull(rowWriter, nameof(rowWriter));
        var snapshot = PostgreSqlBinaryCopySinkOptionsSnapshot.Create(options);

        return PipelineComponent.RuntimeOwned<IPipelineSink<IReadOnlyList<T>>>(
            (_, cancellationToken) => ValueTask.FromResult<IPipelineSink<IReadOnlyList<T>>>(
                new PostgreSqlBinaryCopyBatchSink<T>(
                    validatedDataSource,
                    validatedCommand,
                    rowWriter,
                    snapshot,
                    loggerFactory?.CreateLogger<PostgreSqlBinaryCopyBatchSink<T>>(),
                    cancellationToken)));
    }

    /// <summary>Creates a lazy, per-run <c>LISTEN</c> notification source.</summary>
    /// <param name="dataSource">The application-owned, long-lived data source. Never disposed by SmartPipe.</param>
    /// <param name="channels">
    /// One or more channel identifiers. Identifiers are copied defensively, validated for ordinal duplicates and never
    /// trimmed or case-folded; PostgreSQL owns identifier interpretation.
    /// </param>
    /// <param name="options">The notification source options.</param>
    /// <param name="loggerFactory">An optional borrowed logger factory. Never disposed by SmartPipe.</param>
    /// <returns>A runtime-owned notification source descriptor.</returns>
    public static PipelineComponent<IPipelineSource<PostgreSqlNotification>> NotificationSource(
        NpgsqlDataSource dataSource,
        IReadOnlyCollection<string> channels,
        PostgreSqlNotificationSourceOptions options,
        ILoggerFactory? loggerFactory = null)
    {
        var validatedDataSource = PostgreSqlArguments.NonMultiplexingDataSource(dataSource, PostgreSqlErrorMessages.NotificationMultiplexingUnsupported);
        var channelSet = PostgreSqlChannelSet.Create(channels);
        var snapshot = PostgreSqlNotificationSourceOptionsSnapshot.Create(options);

        return PipelineComponent.RuntimeOwned<IPipelineSource<PostgreSqlNotification>>(
            (_, cancellationToken) => ValueTask.FromResult<IPipelineSource<PostgreSqlNotification>>(
                new PostgreSqlNotificationSource(
                    validatedDataSource,
                    channelSet,
                    snapshot,
                    loggerFactory?.CreateLogger<PostgreSqlNotificationSource>(),
                    cancellationToken)));
    }
}
