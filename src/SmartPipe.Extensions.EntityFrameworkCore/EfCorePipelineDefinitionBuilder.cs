#nullable enable

using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.EntityFrameworkCore;

/// <summary>Starts typed pipeline definitions with an Entity Framework Core query source.</summary>
/// <remarks>
/// Both methods return the ordinary Core <see cref="PipelineDefinitionBuilder{TInput}"/>; stage attachment
/// stays on the normal typed <c>Transform</c> chain and no Entity Framework Core specific typed-source
/// builder exists.
/// </remarks>
[SuppressMessage(
    "ApiDesign",
    "RS0026",
    Justification = "The borrowed-context-factory form and the async-context-factory form are distinguished by their required second parameter; their optional tail is intentionally identical.")]
public static class EfCorePipelineDefinitionBuilder
{
    /// <summary>Starts a typed definition from a queryable source over a borrowed context factory.</summary>
    /// <typeparam name="TContext">The application's <see cref="DbContext"/> type.</typeparam>
    /// <typeparam name="TResult">The projected reference-type result.</typeparam>
    /// <param name="key">The pipeline definition key.</param>
    /// <param name="contextFactory">The borrowed factory that creates one context per run.</param>
    /// <param name="queryFactory">Creates the query for the run; invoked once at activation.</param>
    /// <param name="options">The query options, validated and snapshotted at composition.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory.</param>
    /// <returns>A Core typed definition builder for the projected result.</returns>
    public static PipelineDefinitionBuilder<TResult> FromQuery<TContext, TResult>(
        PipelineKey key,
        IDbContextFactory<TContext> contextFactory,
        Func<TContext, PipelineActivationContext, IQueryable<TResult>> queryFactory,
        EfCoreQueryOptions options,
        ILoggerFactory? loggerFactory = null)
        where TContext : DbContext
        where TResult : class =>
        PipelineDefinitionBuilder.From(
            key,
            EfCorePipelineComponents.QuerySource(contextFactory, queryFactory, options, loggerFactory));

    /// <summary>Starts a typed definition from a queryable source over an async context factory.</summary>
    /// <typeparam name="TContext">The application's <see cref="DbContext"/> type.</typeparam>
    /// <typeparam name="TResult">The projected reference-type result.</typeparam>
    /// <param name="key">The pipeline definition key.</param>
    /// <param name="contextFactory">Creates one fresh context per run.</param>
    /// <param name="queryFactory">Creates the query for the run; invoked once at activation.</param>
    /// <param name="options">The query options, validated and snapshotted at composition.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory.</param>
    /// <returns>A Core typed definition builder for the projected result.</returns>
    public static PipelineDefinitionBuilder<TResult> FromQuery<TContext, TResult>(
        PipelineKey key,
        Func<PipelineActivationContext, CancellationToken, ValueTask<TContext>> contextFactory,
        Func<TContext, PipelineActivationContext, IQueryable<TResult>> queryFactory,
        EfCoreQueryOptions options,
        ILoggerFactory? loggerFactory = null)
        where TContext : DbContext
        where TResult : class =>
        PipelineDefinitionBuilder.From(
            key,
            EfCorePipelineComponents.QuerySource(contextFactory, queryFactory, options, loggerFactory));

    /// <summary>Starts a typed definition from a compiled source over a borrowed context factory.</summary>
    /// <typeparam name="TContext">The application's <see cref="DbContext"/> type.</typeparam>
    /// <typeparam name="TResult">The result type; intentionally unconstrained so scalar and struct results work.</typeparam>
    /// <param name="key">The pipeline definition key.</param>
    /// <param name="contextFactory">The borrowed factory that creates one context per run.</param>
    /// <param name="compiledQuery">The caller's compiled async sequence; invoked once per run.</param>
    /// <param name="options">The compiled-query options, validated and snapshotted at composition.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory.</param>
    /// <returns>A Core typed definition builder for the compiled result.</returns>
    public static PipelineDefinitionBuilder<TResult> FromCompiledQuery<TContext, TResult>(
        PipelineKey key,
        IDbContextFactory<TContext> contextFactory,
        Func<TContext, PipelineActivationContext, CancellationToken, IAsyncEnumerable<TResult>> compiledQuery,
        EfCoreCompiledQueryOptions options,
        ILoggerFactory? loggerFactory = null)
        where TContext : DbContext =>
        PipelineDefinitionBuilder.From(
            key,
            EfCorePipelineComponents.CompiledQuerySource(contextFactory, compiledQuery, options, loggerFactory));

    /// <summary>Starts a typed definition from a compiled source over an async context factory.</summary>
    /// <typeparam name="TContext">The application's <see cref="DbContext"/> type.</typeparam>
    /// <typeparam name="TResult">The result type; intentionally unconstrained so scalar and struct results work.</typeparam>
    /// <param name="key">The pipeline definition key.</param>
    /// <param name="contextFactory">Creates one fresh context per run.</param>
    /// <param name="compiledQuery">The caller's compiled async sequence; invoked once per run.</param>
    /// <param name="options">The compiled-query options, validated and snapshotted at composition.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory.</param>
    /// <returns>A Core typed definition builder for the compiled result.</returns>
    public static PipelineDefinitionBuilder<TResult> FromCompiledQuery<TContext, TResult>(
        PipelineKey key,
        Func<PipelineActivationContext, CancellationToken, ValueTask<TContext>> contextFactory,
        Func<TContext, PipelineActivationContext, CancellationToken, IAsyncEnumerable<TResult>> compiledQuery,
        EfCoreCompiledQueryOptions options,
        ILoggerFactory? loggerFactory = null)
        where TContext : DbContext =>
        PipelineDefinitionBuilder.From(
            key,
            EfCorePipelineComponents.CompiledQuerySource(contextFactory, compiledQuery, options, loggerFactory));
}
