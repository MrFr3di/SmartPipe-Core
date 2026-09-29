#nullable enable

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.EntityFrameworkCore.Runtime;

/// <summary>Streams one Entity Framework Core queryable per run over exactly one owned context.</summary>
/// <remarks>
/// The caller's query factory runs once at activation, the selected tracking operator is applied once, and
/// the query is enumerated once. Everything else — context ownership, enumerator-then-context release,
/// single-enumeration enforcement and primary-first cleanup — is owned by
/// <see cref="EfCoreSourceBase{TContext,TResult}"/>.
/// </remarks>
internal sealed class EfCoreQuerySource<TContext, TResult> : EfCoreSourceBase<TContext, TResult>
    where TContext : DbContext
    where TResult : class
{
    private readonly Func<TContext, PipelineActivationContext, IQueryable<TResult>> _queryFactory;

    private IQueryable<TResult>? _query;

    internal EfCoreQuerySource(
        Func<PipelineActivationContext, CancellationToken, ValueTask<TContext>> createContext,
        Func<TContext, PipelineActivationContext, IQueryable<TResult>> queryFactory,
        EfCoreOptionsSnapshot options,
        PipelineActivationContext activation,
        ILoggerFactory? loggerFactory,
        CancellationToken activationCancellationToken)
        : base(createContext, options, activation, loggerFactory, activationCancellationToken, "query")
    {
        _queryFactory = queryFactory;
    }

    /// <summary>Materializes the run query exactly once, immediately after the context exists.</summary>
    protected override ValueTask OnContextActivatedAsync(TContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = _queryFactory(context, Activation)
            ?? throw new InvalidOperationException("The Entity Framework Core query factory returned no query.");

        _query = ApplyTracking(query, Options.TrackingMode!.Value);
        return ValueTask.CompletedTask;
    }

    /// <summary>Acquires the single enumerator over the tracking-adjusted query.</summary>
    protected override IAsyncEnumerator<TResult> AcquireEnumerator(CancellationToken cancellationToken) =>
        (_query ?? throw new InvalidOperationException("The Entity Framework Core query source has no query for this run."))
            .AsAsyncEnumerable()
            .GetAsyncEnumerator(cancellationToken);

    private static IQueryable<TResult> ApplyTracking(IQueryable<TResult> query, EfCoreQueryTrackingMode trackingMode) =>
        trackingMode switch
        {
            EfCoreQueryTrackingMode.NoTracking => query.AsNoTracking(),
            EfCoreQueryTrackingMode.NoTrackingWithIdentityResolution => query.AsNoTrackingWithIdentityResolution(),
            EfCoreQueryTrackingMode.Tracking => query.AsTracking(),
            EfCoreQueryTrackingMode.PreserveQuery => query,
            _ => throw new ArgumentOutOfRangeException(
                nameof(trackingMode),
                trackingMode,
                "The Entity Framework Core query tracking mode is invalid."),
        };
}
