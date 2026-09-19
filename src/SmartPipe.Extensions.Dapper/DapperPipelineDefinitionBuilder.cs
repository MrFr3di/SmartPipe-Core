#nullable enable

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Dapper;

/// <summary>Starts typed pipeline definitions backed by an explicit-SQL Dapper query source.</summary>
[SuppressMessage(
    "ApiDesign",
    "RS0026",
    Justification = "The borrowed-data-source form and the connection-factory form are distinguished by their required first parameter; their optional tail is intentionally identical.")]
public static class DapperPipelineDefinitionBuilder
{
    /// <summary>Starts a typed definition with a Dapper query source over a borrowed data source.</summary>
    /// <typeparam name="T">The row type produced by the source.</typeparam>
    /// <param name="pipelineKey">The exact pipeline definition key.</param>
    /// <param name="dataSource">The borrowed data source opened once per run.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated query options.</param>
    /// <param name="parametersFactory">Optional per-run parameter object factory.</param>
    /// <param name="rowMapper">Optional reflection-free row mapper for the first result set.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>A typed definition builder.</returns>
    [RequiresUnreferencedCode(DapperPipelineComponents.ReflectionMessage)]
    [RequiresDynamicCode(DapperPipelineComponents.ReflectionMessage)]
    public static PipelineDefinitionBuilder<T> FromQuery<T>(
        PipelineKey pipelineKey,
        DbDataSource dataSource,
        string sql,
        DapperQueryOptions options,
        Func<PipelineActivationContext, object?>? parametersFactory = null,
        Func<DbDataReader, T>? rowMapper = null,
        ILoggerFactory? loggerFactory = null) =>
        SmartPipe.Core.PipelineDefinitionBuilder.From(
            pipelineKey,
            DapperPipelineComponents.QuerySource(
                dataSource,
                sql,
                options,
                parametersFactory,
                rowMapper,
                loggerFactory));

    /// <summary>Starts a typed definition with a Dapper query source over a connection factory.</summary>
    /// <typeparam name="T">The row type produced by the source.</typeparam>
    /// <param name="pipelineKey">The exact pipeline definition key.</param>
    /// <param name="connectionFactory">The per-run connection factory.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated query options.</param>
    /// <param name="parametersFactory">Optional per-run parameter object factory.</param>
    /// <param name="rowMapper">Optional reflection-free row mapper for the first result set.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>A typed definition builder.</returns>
    [RequiresUnreferencedCode(DapperPipelineComponents.ReflectionMessage)]
    [RequiresDynamicCode(DapperPipelineComponents.ReflectionMessage)]
    public static PipelineDefinitionBuilder<T> FromQuery<T>(
        PipelineKey pipelineKey,
        Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> connectionFactory,
        string sql,
        DapperQueryOptions options,
        Func<PipelineActivationContext, object?>? parametersFactory = null,
        Func<DbDataReader, T>? rowMapper = null,
        ILoggerFactory? loggerFactory = null) =>
        SmartPipe.Core.PipelineDefinitionBuilder.From(
            pipelineKey,
            DapperPipelineComponents.QuerySource(
                connectionFactory,
                sql,
                options,
                parametersFactory,
                rowMapper,
                loggerFactory));
}

/// <summary>Completes typed definitions with explicit-SQL Dapper sinks.</summary>
[SuppressMessage(
    "ApiDesign",
    "RS0026",
    Justification = "The borrowed-data-source form and the connection-factory form are distinguished by their required first parameter; their optional tail is intentionally identical.")]
public static class DapperPipelineDefinitionBuilderExtensions
{
    /// <summary>Completes a source-only definition with a Dapper command sink over a borrowed data source.</summary>
    /// <typeparam name="T">The payload type of the written envelope.</typeparam>
    /// <param name="builder">The typed definition builder.</param>
    /// <param name="dataSource">The borrowed data source opened once per run.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated sink options.</param>
    /// <param name="parameterFactory">Optional per-write parameter object factory.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>The completed typed definition.</returns>
    [RequiresUnreferencedCode(DapperPipelineComponents.ReflectionMessage)]
    [RequiresDynamicCode(DapperPipelineComponents.ReflectionMessage)]
    public static PipelineDefinition<T, T> ToCommand<T>(
        this PipelineDefinitionBuilder<T> builder,
        DbDataSource dataSource,
        string sql,
        DapperSinkOptions options,
        Func<ProcessingEnvelope<T>, object?>? parameterFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(
            DapperPipelineComponents.CommandSink(dataSource, sql, options, parameterFactory, loggerFactory));
    }

    /// <summary>Completes a source-only definition with a Dapper command sink over a connection factory.</summary>
    /// <typeparam name="T">The payload type of the written envelope.</typeparam>
    /// <param name="builder">The typed definition builder.</param>
    /// <param name="connectionFactory">The per-run connection factory.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated sink options.</param>
    /// <param name="parameterFactory">Optional per-write parameter object factory.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>The completed typed definition.</returns>
    [RequiresUnreferencedCode(DapperPipelineComponents.ReflectionMessage)]
    [RequiresDynamicCode(DapperPipelineComponents.ReflectionMessage)]
    public static PipelineDefinition<T, T> ToCommand<T>(
        this PipelineDefinitionBuilder<T> builder,
        Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> connectionFactory,
        string sql,
        DapperSinkOptions options,
        Func<ProcessingEnvelope<T>, object?>? parameterFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(
            DapperPipelineComponents.CommandSink(
                connectionFactory,
                sql,
                options,
                parameterFactory,
                loggerFactory));
    }

    /// <summary>Completes a multi-stage definition with a Dapper command sink over a borrowed data source.</summary>
    /// <typeparam name="TPipelineInput">The pipeline input type.</typeparam>
    /// <typeparam name="TCurrent">The current stage payload type.</typeparam>
    /// <param name="builder">The typed definition builder.</param>
    /// <param name="dataSource">The borrowed data source opened once per run.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated sink options.</param>
    /// <param name="parameterFactory">Optional per-write parameter object factory.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>The completed typed definition.</returns>
    [RequiresUnreferencedCode(DapperPipelineComponents.ReflectionMessage)]
    [RequiresDynamicCode(DapperPipelineComponents.ReflectionMessage)]
    public static PipelineDefinition<TPipelineInput, TCurrent> ToCommand<TPipelineInput, TCurrent>(
        this PipelineDefinitionBuilder<TPipelineInput, TCurrent> builder,
        DbDataSource dataSource,
        string sql,
        DapperSinkOptions options,
        Func<ProcessingEnvelope<TCurrent>, object?>? parameterFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(
            DapperPipelineComponents.CommandSink(dataSource, sql, options, parameterFactory, loggerFactory));
    }

    /// <summary>Completes a multi-stage definition with a Dapper command sink over a connection factory.</summary>
    /// <typeparam name="TPipelineInput">The pipeline input type.</typeparam>
    /// <typeparam name="TCurrent">The current stage payload type.</typeparam>
    /// <param name="builder">The typed definition builder.</param>
    /// <param name="connectionFactory">The per-run connection factory.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated sink options.</param>
    /// <param name="parameterFactory">Optional per-write parameter object factory.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>The completed typed definition.</returns>
    [RequiresUnreferencedCode(DapperPipelineComponents.ReflectionMessage)]
    [RequiresDynamicCode(DapperPipelineComponents.ReflectionMessage)]
    public static PipelineDefinition<TPipelineInput, TCurrent> ToCommand<TPipelineInput, TCurrent>(
        this PipelineDefinitionBuilder<TPipelineInput, TCurrent> builder,
        Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> connectionFactory,
        string sql,
        DapperSinkOptions options,
        Func<ProcessingEnvelope<TCurrent>, object?>? parameterFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(
            DapperPipelineComponents.CommandSink(
                connectionFactory,
                sql,
                options,
                parameterFactory,
                loggerFactory));
    }

    /// <summary>Completes a batch definition with a Dapper batch sink over a borrowed data source.</summary>
    /// <typeparam name="T">The item type of the batch payload.</typeparam>
    /// <param name="builder">The typed definition builder.</param>
    /// <param name="dataSource">The borrowed data source opened once per run.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated batch sink options.</param>
    /// <param name="itemParameterFactory">Optional per-item parameter object factory.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>The completed batch definition.</returns>
    [RequiresUnreferencedCode(DapperPipelineComponents.ReflectionMessage)]
    [RequiresDynamicCode(DapperPipelineComponents.ReflectionMessage)]
    public static PipelineDefinition<IReadOnlyList<T>, IReadOnlyList<T>> ToBatchCommand<T>(
        this PipelineDefinitionBuilder<IReadOnlyList<T>> builder,
        DbDataSource dataSource,
        string sql,
        DapperBatchSinkOptions options,
        Func<T, object?>? itemParameterFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(
            DapperPipelineComponents.BatchCommandSink(
                dataSource,
                sql,
                options,
                itemParameterFactory,
                loggerFactory));
    }

    /// <summary>Completes a batch definition with a Dapper batch sink over a connection factory.</summary>
    /// <typeparam name="T">The item type of the batch payload.</typeparam>
    /// <param name="builder">The typed definition builder.</param>
    /// <param name="connectionFactory">The per-run connection factory.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated batch sink options.</param>
    /// <param name="itemParameterFactory">Optional per-item parameter object factory.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>The completed batch definition.</returns>
    [RequiresUnreferencedCode(DapperPipelineComponents.ReflectionMessage)]
    [RequiresDynamicCode(DapperPipelineComponents.ReflectionMessage)]
    public static PipelineDefinition<IReadOnlyList<T>, IReadOnlyList<T>> ToBatchCommand<T>(
        this PipelineDefinitionBuilder<IReadOnlyList<T>> builder,
        Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> connectionFactory,
        string sql,
        DapperBatchSinkOptions options,
        Func<T, object?>? itemParameterFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(
            DapperPipelineComponents.BatchCommandSink(
                connectionFactory,
                sql,
                options,
                itemParameterFactory,
                loggerFactory));
    }

    /// <summary>Completes a multi-stage definition with a Dapper batch sink over a borrowed data source.</summary>
    /// <typeparam name="TPipelineInput">The pipeline input type.</typeparam>
    /// <typeparam name="T">The item type of the batch payload.</typeparam>
    /// <param name="builder">The typed definition builder.</param>
    /// <param name="dataSource">The borrowed data source opened once per run.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated batch sink options.</param>
    /// <param name="itemParameterFactory">Optional per-item parameter object factory.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>The completed batch definition.</returns>
    [RequiresUnreferencedCode(DapperPipelineComponents.ReflectionMessage)]
    [RequiresDynamicCode(DapperPipelineComponents.ReflectionMessage)]
    public static PipelineDefinition<TPipelineInput, IReadOnlyList<T>> ToBatchCommand<TPipelineInput, T>(
        this PipelineDefinitionBuilder<TPipelineInput, IReadOnlyList<T>> builder,
        DbDataSource dataSource,
        string sql,
        DapperBatchSinkOptions options,
        Func<T, object?>? itemParameterFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(
            DapperPipelineComponents.BatchCommandSink(
                dataSource,
                sql,
                options,
                itemParameterFactory,
                loggerFactory));
    }

    /// <summary>Completes a multi-stage definition with a Dapper batch sink over a connection factory.</summary>
    /// <typeparam name="TPipelineInput">The pipeline input type.</typeparam>
    /// <typeparam name="T">The item type of the batch payload.</typeparam>
    /// <param name="builder">The typed definition builder.</param>
    /// <param name="connectionFactory">The per-run connection factory.</param>
    /// <param name="sql">The explicit SQL text to execute.</param>
    /// <param name="options">The validated batch sink options.</param>
    /// <param name="itemParameterFactory">Optional per-item parameter object factory.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory. It is never disposed.</param>
    /// <returns>The completed batch definition.</returns>
    [RequiresUnreferencedCode(DapperPipelineComponents.ReflectionMessage)]
    [RequiresDynamicCode(DapperPipelineComponents.ReflectionMessage)]
    public static PipelineDefinition<TPipelineInput, IReadOnlyList<T>> ToBatchCommand<TPipelineInput, T>(
        this PipelineDefinitionBuilder<TPipelineInput, IReadOnlyList<T>> builder,
        Func<PipelineActivationContext, CancellationToken, ValueTask<DbConnection>> connectionFactory,
        string sql,
        DapperBatchSinkOptions options,
        Func<T, object?>? itemParameterFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(
            DapperPipelineComponents.BatchCommandSink(
                connectionFactory,
                sql,
                options,
                itemParameterFactory,
                loggerFactory));
    }
}
