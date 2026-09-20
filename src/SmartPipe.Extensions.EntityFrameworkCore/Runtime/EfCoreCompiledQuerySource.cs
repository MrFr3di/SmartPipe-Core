#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.EntityFrameworkCore.Runtime;

/// <summary>Streams one caller-provided compiled async sequence per run over exactly one owned context.</summary>
/// <remarks>
/// The result type is intentionally unconstrained, so scalar and struct results are supported. There is no
/// tracking operator on this path because the compiled delegate already fixes the query shape. The context
/// is created once at activation, the compiled delegate runs once per run, and the enumerator is released
/// before the context, both exactly once.
/// </remarks>
internal sealed class EfCoreCompiledQuerySource<TContext, TResult> : IPipelineSource<TResult>
    where TContext : DbContext
{
    private readonly Func<PipelineActivationContext, CancellationToken, ValueTask<TContext>> _createContext;
    private readonly Func<TContext, PipelineActivationContext, CancellationToken, IAsyncEnumerable<TResult>> _compiledQuery;
    private readonly EfCoreOptionsSnapshot _options;
    private readonly PipelineActivationContext _activation;
    private readonly ILogger? _logger;
    private readonly CancellationToken _activationCancellationToken;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly EfCoreSingleFlightDisposal _disposal = new();
    private readonly EfCoreSingleFlightDisposal _dispose = new();

    private TContext? _context;
    private bool _initialized;
    private int _enumerated;
    private bool _disposed;

    internal EfCoreCompiledQuerySource(
        Func<PipelineActivationContext, CancellationToken, ValueTask<TContext>> createContext,
        Func<TContext, PipelineActivationContext, CancellationToken, IAsyncEnumerable<TResult>> compiledQuery,
        EfCoreOptionsSnapshot options,
        PipelineActivationContext activation,
        ILoggerFactory? loggerFactory,
        CancellationToken activationCancellationToken)
    {
        _createContext = createContext;
        _compiledQuery = compiledQuery;
        _options = options;
        _activation = activation;
        _logger = loggerFactory?.CreateLogger<EfCoreCompiledQuerySource<TContext, TResult>>();
        _activationCancellationToken = activationCancellationToken;
    }

    /// <summary>Creates the single per-run context exactly once.</summary>
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

            _context = await _createContext(_activation, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Entity Framework Core context factory returned no context.");
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    /// <summary>Streams the single compiled sequence and releases the enumerator then the context exactly once.</summary>
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
            throw new InvalidOperationException("The Entity Framework Core compiled query source supports exactly one enumeration per run.");

        var context = _context
            ?? throw new InvalidOperationException("The Entity Framework Core compiled query source has no context for this run.");
        var startedTimestamp = _activation.TimeProvider.GetTimestamp();
        var itemCount = 0L;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();

        try
        {
            IAsyncEnumerable<TResult>? sequence = null;
            try
            {
                sequence = _compiledQuery(context, _activation, cancellationToken);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }

            if (sequence is null && primaryFailure is null)
            {
                primaryFailure = new InvalidOperationException(
                    "The Entity Framework Core compiled query returned no async sequence.");
            }

            if (sequence is not null)
            {
                IAsyncEnumerator<TResult>? enumerator = null;
                try
                {
                    enumerator = sequence.GetAsyncEnumerator(cancellationToken);
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
        }
        finally
        {
            cleanupFailures.AddRange(await ReleaseAsync().ConfigureAwait(false));
            EfCoreLogging.LogOutcome(
                _logger,
                _activation,
                _options.OperationName,
                typeof(TResult).Name,
                "compiled query",
                itemCount,
                _activation.TimeProvider.GetElapsedTime(startedTimestamp),
                primaryFailure);
            EfCoreCleanup.ThrowPrimaryFirst(
                primaryFailure,
                cleanupFailures,
                "The Entity Framework Core compiled query failed and cleanup also failed.");
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
                "Disposing the Entity Framework Core compiled query source failed.");
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private async Task<List<Exception>> ReleaseAsync()
    {
        var cleanupFailures = new List<Exception>();
        await _disposal.DisposeAsync(async () =>
        {
            var context = _context;
            _context = null;
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
