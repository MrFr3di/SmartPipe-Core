#nullable enable

using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;
using SmartPipe.Extensions.EntityFrameworkCore.Runtime;

namespace SmartPipe.Extensions.EntityFrameworkCore;

/// <summary>Creates provider-neutral Entity Framework Core query source descriptors.</summary>
/// <remarks>
/// Descriptor creation never invokes a context factory or a query factory and performs no I/O. Every
/// descriptor is runtime-owned: each run creates exactly one context, owns it, and releases it exactly
/// once. The package depends on <c>Microsoft.EntityFrameworkCore</c> only and never on a provider.
/// </remarks>
[SuppressMessage(
    "ApiDesign",
    "RS0026",
    Justification = "The borrowed-context-factory form and the async-context-factory form are distinguished by their required first parameter; their optional tail is intentionally identical.")]
public static class EfCorePipelineComponents
{
    /// <summary>Creates a queryable source over a borrowed context factory.</summary>
    /// <typeparam name="TContext">The application's <see cref="DbContext"/> type.</typeparam>
    /// <typeparam name="TResult">The projected reference-type result.</typeparam>
    /// <param name="contextFactory">The borrowed factory that creates one context per run.</param>
    /// <param name="queryFactory">Creates the query for the run; invoked once at activation.</param>
    /// <param name="options">The query options, validated and snapshotted at composition.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory.</param>
    /// <returns>A runtime-owned query source descriptor.</returns>
    public static PipelineComponent<IPipelineSource<TResult>> QuerySource<TContext, TResult>(
        IDbContextFactory<TContext> contextFactory,
        Func<TContext, PipelineActivationContext, IQueryable<TResult>> queryFactory,
        EfCoreQueryOptions options,
        ILoggerFactory? loggerFactory = null)
        where TContext : DbContext
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        return QuerySource<TContext, TResult>(
            (activation, cancellationToken) => new ValueTask<TContext>(contextFactory.CreateDbContextAsync(cancellationToken)),
            queryFactory,
            options,
            loggerFactory);
    }

    /// <summary>Creates a queryable source over a caller-supplied async context factory.</summary>
    /// <typeparam name="TContext">The application's <see cref="DbContext"/> type.</typeparam>
    /// <typeparam name="TResult">The projected reference-type result.</typeparam>
    /// <param name="contextFactory">Creates one fresh context per run.</param>
    /// <param name="queryFactory">Creates the query for the run; invoked once at activation.</param>
    /// <param name="options">The query options, validated and snapshotted at composition.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory.</param>
    /// <returns>A runtime-owned query source descriptor.</returns>
    public static PipelineComponent<IPipelineSource<TResult>> QuerySource<TContext, TResult>(
        Func<PipelineActivationContext, CancellationToken, ValueTask<TContext>> contextFactory,
        Func<TContext, PipelineActivationContext, IQueryable<TResult>> queryFactory,
        EfCoreQueryOptions options,
        ILoggerFactory? loggerFactory = null)
        where TContext : DbContext
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(queryFactory);
        var snapshot = EfCoreOptionsSnapshot.Create(options);
        return PipelineComponent.RuntimeOwned<IPipelineSource<TResult>>(
            (activation, cancellationToken) => new ValueTask<IPipelineSource<TResult>>(
                new EfCoreQuerySource<TContext, TResult>(
                    contextFactory,
                    queryFactory,
                    snapshot,
                    activation,
                    loggerFactory,
                    cancellationToken)));
    }

    /// <summary>Creates a compiled or caller-provided async-sequence source over a borrowed context factory.</summary>
    /// <typeparam name="TContext">The application's <see cref="DbContext"/> type.</typeparam>
    /// <typeparam name="TResult">The result type; intentionally unconstrained so scalar and struct results work.</typeparam>
    /// <param name="contextFactory">The borrowed factory that creates one context per run.</param>
    /// <param name="compiledQuery">The caller's compiled async sequence; invoked once per run.</param>
    /// <param name="options">The compiled-query options, validated and snapshotted at composition.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory.</param>
    /// <returns>A runtime-owned compiled query source descriptor.</returns>
    public static PipelineComponent<IPipelineSource<TResult>> CompiledQuerySource<TContext, TResult>(
        IDbContextFactory<TContext> contextFactory,
        Func<TContext, PipelineActivationContext, CancellationToken, IAsyncEnumerable<TResult>> compiledQuery,
        EfCoreCompiledQueryOptions options,
        ILoggerFactory? loggerFactory = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        return CompiledQuerySource<TContext, TResult>(
            (activation, cancellationToken) => new ValueTask<TContext>(contextFactory.CreateDbContextAsync(cancellationToken)),
            compiledQuery,
            options,
            loggerFactory);
    }

    /// <summary>Creates a compiled or caller-provided async-sequence source over an async context factory.</summary>
    /// <typeparam name="TContext">The application's <see cref="DbContext"/> type.</typeparam>
    /// <typeparam name="TResult">The result type; intentionally unconstrained so scalar and struct results work.</typeparam>
    /// <param name="contextFactory">Creates one fresh context per run.</param>
    /// <param name="compiledQuery">The caller's compiled async sequence; invoked once per run.</param>
    /// <param name="options">The compiled-query options, validated and snapshotted at composition.</param>
    /// <param name="loggerFactory">Optional borrowed logger factory.</param>
    /// <returns>A runtime-owned compiled query source descriptor.</returns>
    public static PipelineComponent<IPipelineSource<TResult>> CompiledQuerySource<TContext, TResult>(
        Func<PipelineActivationContext, CancellationToken, ValueTask<TContext>> contextFactory,
        Func<TContext, PipelineActivationContext, CancellationToken, IAsyncEnumerable<TResult>> compiledQuery,
        EfCoreCompiledQueryOptions options,
        ILoggerFactory? loggerFactory = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(compiledQuery);
        var snapshot = EfCoreOptionsSnapshot.Create(options);
        return PipelineComponent.RuntimeOwned<IPipelineSource<TResult>>(
            (activation, cancellationToken) => new ValueTask<IPipelineSource<TResult>>(
                new EfCoreCompiledQuerySource<TContext, TResult>(
                    contextFactory,
                    compiledQuery,
                    snapshot,
                    activation,
                    loggerFactory,
                    cancellationToken)));
    }
}
