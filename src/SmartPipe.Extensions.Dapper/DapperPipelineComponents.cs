#nullable enable

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;
using SmartPipe.Extensions.Dapper.Internal;

namespace SmartPipe.Extensions.Dapper;

/// <summary>Creates runtime-owned explicit-SQL Dapper pipeline components.</summary>
/// <remarks>
/// Composing a component performs no I/O: it opens no connection, creates no command, begins no
/// transaction and executes no SQL. The acquisition parameter is stored and borrowed once per run.
/// </remarks>
[SuppressMessage(
    "ApiDesign",
    "RS0026",
    Justification = "The borrowed-data-source form and the connection-factory form are distinguished by their required first parameter; their optional tail is intentionally identical.")]
public static class DapperPipelineComponents
{
    internal const string ReflectionMessage =
        "Dapper runtime mapping and parameter binding use reflection and runtime code generation.";

    /// <summary>Creates a lazy, per-run Dapper query source over a borrowed data source.</summary>
    /// <typeparam name="T">The row type produced by the source.</typeparam>
    /// <param name="dataSource">The borrowed data source opened once per run.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated query options.</param>
    /// <param name="parametersFactory">Optional per-run parameter object factory.</param>
    /// <param name="rowMapper">Optional reflection-free row mapper for the first result set.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>A runtime-owned source descriptor.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static PipelineComponent<IPipelineSource<T>> QuerySource<T>(
        DbDataSource dataSource,
        string sql,
        DapperQueryOptions options,
        Func<PipelineActivationContext, object?>? parametersFactory = null,
        Func<DbDataReader, T>? rowMapper = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return QuerySource<T>(
            (_, cancellationToken) => dataSource.OpenConnectionAsync(cancellationToken),
            sql,
            options,
            parametersFactory,
            rowMapper,
            loggerFactory);
    }

    /// <summary>Creates a lazy, per-run Dapper query source over a connection factory.</summary>
    /// <typeparam name="T">The row type produced by the source.</typeparam>
    /// <param name="connectionFactory">The per-run connection factory.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated query options.</param>
    /// <param name="parametersFactory">Optional per-run parameter object factory.</param>
    /// <param name="rowMapper">Optional reflection-free row mapper for the first result set.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>A runtime-owned source descriptor.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static PipelineComponent<IPipelineSource<T>> QuerySource<T>(
        Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> connectionFactory,
        string sql,
        DapperQueryOptions options,
        Func<PipelineActivationContext, object?>? parametersFactory = null,
        Func<DbDataReader, T>? rowMapper = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        var validatedSql = DapperOptionsValidation.ValidateSql(sql, nameof(sql));
        var snapshot = DapperQueryOptionsSnapshot.Create(options);

        return PipelineComponent.RuntimeOwned<IPipelineSource<T>>(
            (context, cancellationToken) => ValueTask.FromResult<IPipelineSource<T>>(
                new DapperQuerySource<T>(
                    connectionFactory,
                    context,
                    validatedSql,
                    snapshot,
                    parametersFactory,
                    rowMapper,
                    loggerFactory?.CreateLogger<DapperQuerySource<T>>(),
                    cancellationToken)));
    }

    /// <summary>Creates a lazy, per-run Dapper command sink over a borrowed data source.</summary>
    /// <typeparam name="T">The payload type of the written envelope.</typeparam>
    /// <param name="dataSource">The borrowed data source opened once per run.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated sink options.</param>
    /// <param name="parameterFactory">Optional per-write parameter object factory.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>A runtime-owned sink descriptor.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static PipelineComponent<IPipelineSink<T>> CommandSink<T>(
        DbDataSource dataSource,
        string sql,
        DapperSinkOptions options,
        Func<ProcessingEnvelope<T>, object?>? parameterFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return CommandSink(
            (_, cancellationToken) => dataSource.OpenConnectionAsync(cancellationToken),
            sql,
            options,
            parameterFactory,
            loggerFactory);
    }

    /// <summary>Creates a lazy, per-run Dapper command sink over a connection factory.</summary>
    /// <typeparam name="T">The payload type of the written envelope.</typeparam>
    /// <param name="connectionFactory">The per-run connection factory.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated sink options.</param>
    /// <param name="parameterFactory">Optional per-write parameter object factory.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>A runtime-owned sink descriptor.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static PipelineComponent<IPipelineSink<T>> CommandSink<T>(
        Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> connectionFactory,
        string sql,
        DapperSinkOptions options,
        Func<ProcessingEnvelope<T>, object?>? parameterFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        var validatedSql = DapperOptionsValidation.ValidateSql(sql, nameof(sql));
        var snapshot = DapperSinkOptionsSnapshot.Create(options);

        return PipelineComponent.RuntimeOwned<IPipelineSink<T>>(
            (context, cancellationToken) => ValueTask.FromResult<IPipelineSink<T>>(
                new DapperCommandSink<T>(
                    connectionFactory,
                    context,
                    validatedSql,
                    snapshot,
                    parameterFactory,
                    loggerFactory?.CreateLogger<DapperCommandSink<T>>(),
                    cancellationToken)));
    }

    /// <summary>Creates a lazy, per-run Dapper batch command sink over a borrowed data source.</summary>
    /// <typeparam name="T">The item type of the batch payload.</typeparam>
    /// <param name="dataSource">The borrowed data source opened once per run.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated batch sink options.</param>
    /// <param name="itemParameterFactory">Optional per-item parameter object factory.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>A runtime-owned batch sink descriptor.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static PipelineComponent<IPipelineSink<IReadOnlyList<T>>> BatchCommandSink<T>(
        DbDataSource dataSource,
        string sql,
        DapperBatchSinkOptions options,
        Func<T, object?>? itemParameterFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return BatchCommandSink(
            (_, cancellationToken) => dataSource.OpenConnectionAsync(cancellationToken),
            sql,
            options,
            itemParameterFactory,
            loggerFactory);
    }

    /// <summary>Creates a lazy, per-run Dapper batch command sink over a connection factory.</summary>
    /// <typeparam name="T">The item type of the batch payload.</typeparam>
    /// <param name="connectionFactory">The per-run connection factory.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated batch sink options.</param>
    /// <param name="itemParameterFactory">Optional per-item parameter object factory.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>A runtime-owned batch sink descriptor.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(ReflectionMessage)]
    public static PipelineComponent<IPipelineSink<IReadOnlyList<T>>> BatchCommandSink<T>(
        Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> connectionFactory,
        string sql,
        DapperBatchSinkOptions options,
        Func<T, object?>? itemParameterFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        var validatedSql = DapperOptionsValidation.ValidateSql(sql, nameof(sql));
        var snapshot = DapperBatchSinkOptionsSnapshot.Create(options);

        return PipelineComponent.RuntimeOwned<IPipelineSink<IReadOnlyList<T>>>(
            (context, cancellationToken) => ValueTask.FromResult<IPipelineSink<IReadOnlyList<T>>>(
                new DapperBatchCommandSink<T>(
                    connectionFactory,
                    context,
                    validatedSql,
                    snapshot,
                    itemParameterFactory,
                    loggerFactory?.CreateLogger<DapperBatchCommandSink<T>>(),
                    cancellationToken)));
    }
}
