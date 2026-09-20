#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.EntityFrameworkCore.Runtime;

/// <summary>Streams one Entity Framework Core queryable per run over exactly one owned context.</summary>
/// <remarks>
/// The context is created once at activation, the caller's query factory runs once per run, the selected
/// tracking operator is applied once, and the query is enumerated once. The enumerator is released before
/// the context, both exactly once, and a primary failure is never replaced by a cleanup failure.
/// </remarks>
internal sealed class EfCoreQuerySource<TContext, TResult> : IPipelineSource<TResult>
    where TContext : DbContext
    where TResult : class
{
    private readonly Func<PipelineActivationContext, CancellationToken, ValueTask<TContext>> _createContext;
    private readonly Func<TContext, PipelineActivationContext, IQueryable<TResult>> _queryFactory;
    private readonly EfCoreOptionsSnapshot _options;
    private readonly PipelineActivationContext _activation;
    private readonly ILogger? _logger;
    private readonly CancellationToken _activationCancellationToken;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly EfCoreSingleFlightDisposal _disposal = new();
    private readonly EfCoreSingleFlightDisposal _dispose = new();

    private TContext? _context;
    private IQueryable<TResult>? _query;
    private bool _initialized;
    private int _enumerated;
    private bool _disposed;

    internal EfCoreQuerySource(
        Func<PipelineActivationContext, CancellationToken, ValueTask<TContext>> createContext,
        Func<TContext, PipelineActivationContext, IQueryable<TResult>> queryFactory,
        EfCoreOptionsSnapshot options,
        PipelineActivationContext activation,
        ILoggerFactory? loggerFactory,
        CancellationToken activationCancellationToken)
    {
        _createContext = createContext;
        _queryFactory = queryFactory;
        _options = options;
        _activation = activation;
        _logger = loggerFactory?.CreateLogger<EfCoreQuerySource<TContext, TResult>>();
        _activationCancellationToken = activationCancellationToken;
    }

    /// <summary>Creates the single per-run context and materializes the run query exactly once.</summary>
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        await _initializeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
                return;

            using var linkedCancellation = EfCoreCancellation.CreateLinked(
                _activationCancellationToken,
                ct,
                out var cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var context = await _createContext(_activation, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Entity Framework Core context factory returned no context.");

            try
            {
                var query = _queryFactory(context, _activation)
                    ?? throw new InvalidOperationException("The Entity Framework Core query factory returned no query.");
                _query = ApplyTracking(query, _options.TrackingMode!.Value);
            }
            catch (Exception primaryFailure)
            {
                var cleanupFailures = new List<Exception>();
                try
                {
                    await context.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }

                EfCoreCleanup.ThrowPrimaryFirst(
                    primaryFailure,
                    cleanupFailures,
                    "The Entity Framework Core query factory failed and cleanup also failed.");
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }

            _context = context;
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    /// <summary>Streams the single query and releases the enumerator then the context exactly once.</summary>
    public async IAsyncEnumerable<ProcessingEnvelope<TResult>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linkedCancellation = EfCoreCancellation.CreateLinked(
            _activationCancellationToken,
            ct,
            out var cancellationToken);
        if (!_initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (Interlocked.Exchange(ref _enumerated, 1) != 0)
            throw new InvalidOperationException("The Entity Framework Core query source supports exactly one enumeration per run.");

        var query = _query
            ?? throw new InvalidOperationException("The Entity Framework Core query source has no query for this run.");
        var startedTimestamp = _activation.TimeProvider.GetTimestamp();
        var itemCount = 0L;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();

        try
        {
            IAsyncEnumerator<TResult>? enumerator = null;
            try
            {
                enumerator = query
                    .AsAsyncEnumerable()
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }

            if (enumerator is null)
                yield break;

            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        primaryFailure = exception;
                        break;
                    }

                    if (!hasNext)
                        break;

                    ProcessingEnvelope<TResult> envelope;
                    try
                    {
                        envelope = ProcessingEnvelope<TResult>.Create(enumerator.Current);
                    }
                    catch (Exception exception)
                    {
                        primaryFailure = exception;
                        break;
                    }

                    itemCount++;
                    yield return envelope;
                }
            }
            finally
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }
        }
        finally
        {
            cleanupFailures.AddRange(await ReleaseAsync().ConfigureAwait(false));
            EfCoreLogging.LogOutcome(
                _logger,
                _activation,
                _options.OperationName,
                typeof(TResult).Name,
                "query",
                itemCount,
                _activation.TimeProvider.GetElapsedTime(startedTimestamp),
                primaryFailure);
            EfCoreCleanup.ThrowPrimaryFirst(
                primaryFailure,
                cleanupFailures,
                "The Entity Framework Core query failed and cleanup also failed.");
            if (primaryFailure is not null)
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    /// <summary>Releases the run context exactly once and stays idempotent across repeated calls.</summary>
    public ValueTask DisposeAsync() => _dispose.DisposeAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        await _initializeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            var cleanupFailures = await ReleaseAsync().ConfigureAwait(false);
            EfCoreCleanup.ThrowPrimaryFirst(
                null,
                cleanupFailures,
                "Disposing the Entity Framework Core query source failed.");
        }
        finally
        {
            _initializeGate.Release();
        }
    }

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

    private async Task<List<Exception>> ReleaseAsync()
    {
        var cleanupFailures = new List<Exception>();
        await _disposal.DisposeAsync(async () =>
        {
            var context = _context;
            _context = null;
            _query = null;
            if (context is null)
                return;

            try
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }).ConfigureAwait(false);

        return cleanupFailures;
    }
}
