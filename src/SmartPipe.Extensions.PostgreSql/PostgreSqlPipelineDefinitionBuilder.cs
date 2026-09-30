using Microsoft.Extensions.Logging;
using Npgsql;
using SmartPipe.Core;

namespace SmartPipe.Extensions.PostgreSql;

/// <summary>Starts typed pipeline definitions backed by PostgreSQL-native sources.</summary>
public static class PostgreSqlPipelineDefinitionBuilder
{
    /// <summary>Starts a typed definition with a binary <c>COPY … TO STDOUT (FORMAT BINARY)</c> source.</summary>
    /// <typeparam name="T">The row value produced by <paramref name="rowReader"/>.</typeparam>
    /// <param name="pipelineKey">The pipeline key.</param>
    /// <param name="dataSource">The application-owned, long-lived data source. Never disposed by SmartPipe.</param>
    /// <param name="copyToCommand">The complete configuration COPY statement sent to PostgreSQL.</param>
    /// <param name="rowReader">Reads exactly one row from the exporter.</param>
    /// <param name="options">The COPY source options.</param>
    /// <param name="loggerFactory">An optional borrowed logger factory. Never disposed by SmartPipe.</param>
    /// <returns>A definition builder whose source streams PostgreSQL rows.</returns>
    public static PipelineDefinitionBuilder<T> FromBinaryCopy<T>(
        PipelineKey pipelineKey,
        NpgsqlDataSource dataSource,
        string copyToCommand,
        Func<NpgsqlBinaryExporter, int, CancellationToken, ValueTask<T>> rowReader,
        PostgreSqlBinaryCopySourceOptions options,
        ILoggerFactory? loggerFactory = null) =>
        SmartPipe.Core.PipelineDefinitionBuilder.From(
            pipelineKey,
            PostgreSqlPipelineComponents.BinaryCopySource(dataSource, copyToCommand, rowReader, options, loggerFactory));

    /// <summary>Starts a definition with a <c>LISTEN</c> notification source.</summary>
    /// <param name="pipelineKey">The pipeline key.</param>
    /// <param name="dataSource">The application-owned, long-lived data source. Never disposed by SmartPipe.</param>
    /// <param name="channels">One or more channel identifiers.</param>
    /// <param name="options">The notification source options.</param>
    /// <param name="loggerFactory">An optional borrowed logger factory. Never disposed by SmartPipe.</param>
    /// <returns>A definition builder whose source emits accepted notifications.</returns>
    public static PipelineDefinitionBuilder<PostgreSqlNotification> FromNotifications(
        PipelineKey pipelineKey,
        NpgsqlDataSource dataSource,
        IReadOnlyCollection<string> channels,
        PostgreSqlNotificationSourceOptions options,
        ILoggerFactory? loggerFactory = null) =>
        SmartPipe.Core.PipelineDefinitionBuilder.From(
            pipelineKey,
            PostgreSqlPipelineComponents.NotificationSource(dataSource, channels, options, loggerFactory));
}
