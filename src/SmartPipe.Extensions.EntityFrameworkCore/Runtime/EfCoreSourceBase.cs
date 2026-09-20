#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.EntityFrameworkCore.Runtime;

/// <summary>Owns the per-run context and the shared enumeration, cleanup and logging machinery.</summary>
/// <remarks>
/// The context is created once at activation and released exactly once, the enumerator is released before
/// the context, exactly one enumeration per run is allowed, and a primary failure is never replaced by a
/// cleanup failure. Derived sources only decide how the run acquires its single enumerator and what extra
/// work activation performs.
/// </remarks>
internal abstract class EfCoreSourceBase<TContext, TResult> : IPipelineSource<TResult>
    where TContext : DbContext
{
    private readonly Func<PipelineActivationContext, CancellationToken, ValueTask<TContext>> _createContext;
    private readonly string _logKind;
    private readonly ILogger? _logger;
    private readonly CancellationToken _activationCancellationToken;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly EfCoreSingleFlightDisposal _contextRelease = new();
    private readonly EfCoreSingleFlightDisposal _dispose = new();

    private TContext? _context;
    private bool _initialized;
    private int _enumerated;
    private bool _disposed;

    protected EfCoreSourceBase(
        Func<PipelineActivationContext, CancellationToken, ValueTask<TContext>> createContext,
        EfCoreOptionsSnapshot options,
        PipelineActivationContext activation,
        ILoggerFactory? loggerFactory,
        CancellationToken activationCancellationToken,
        string logKind)
    {
        _createContext = createContext;
        Options = options;
        Activation = activation;
        _logger = loggerFactory?.CreateLogger(GetType());
        _activationCancellationToken = activationCancellationToken;
        _logKind = logKind;
    }

    /// <summary>Gets the validated options snapshot for this descriptor.</summary>
    protected EfCoreOptionsSnapshot Options { get; }

    /// <summary>Gets the activation context of the run.</summary>
    protected PipelineActivationContext Activation { get; }

    /// <summary>Gets the run context, which is valid only between activation and release.</summary>
    protected TContext Context =>
        _context ?? throw new InvalidOperationException("The Entity Framework Core source has no context for this run.");

    /// <summary>Creates the single per-run context and runs the source-specific activation work once.</summary>
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

            var context = await _createContext(Activation, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Entity Framework Core context factory returned no context.");

            try
            {
                await OnContextActivatedAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception primaryFailure)
            {
                var cleanupFailures = new List<Exception>();
                await DisposeContextAsync(context, cleanupFailures).ConfigureAwait(false);
                EfCoreCleanup.ThrowPrimaryFirst(
                    primaryFailure,
                    cleanupFailures,
                    "The Entity Framework Core source failed during activation and cleanup also failed.");
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

    /// <summary>Performs the source-specific activation work after the context exists.</summary>
    protected virtual ValueTask OnContextActivatedAsync(TContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>Acquires the single enumerator for this run.</summary>
    protected abstract IAsyncEnumerator<TResult> AcquireEnumerator(CancellationToken cancellationToken);

    /// <summary>Streams the single enumeration and releases the enumerator then the context exactly once.</summary>
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
        {
            throw new InvalidOperationException(
                $"The Entity Framework Core {_logKind} source supports exactly one enumeration per run.");
        }

        var startedTimestamp = Activation.TimeProvider.GetTimestamp();
        var itemCount = 0L;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();

        try
        {
            IAsyncEnumerator<TResult>? enumerator = null;
            try
            {
                enumerator = AcquireEnumerator(cancellationToken);
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
                    var (hasNext, moveFailure) = await TryMoveNextAsync(enumerator).ConfigureAwait(false);
                    if (moveFailure is not null)
                    {
                        primaryFailure = moveFailure;
                        break;
                    }

                    if (!hasNext)
                        break;

                    var (envelope, createFailure) = TryCreateEnvelope(enumerator.Current);
                    if (createFailure is not null)
                    {
                        primaryFailure = createFailure;
                        break;
                    }

                    itemCount++;
                    yield return envelope!;
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
            cleanupFailures.AddRange(await ReleaseContextAsync().ConfigureAwait(false));
            EfCoreLogging.LogOutcome(
                _logger,
                Activation,
                new EfCoreOutcome(
                    _logKind,
                    Options.OperationName,
                    typeof(TResult).Name,
                    itemCount,
                    Activation.TimeProvider.GetElapsedTime(startedTimestamp)),
                primaryFailure);
            EfCoreCleanup.ThrowPrimaryFirst(
                primaryFailure,
                cleanupFailures,
                $"The Entity Framework Core {_logKind} failed and cleanup also failed.");
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
            var cleanupFailures = await ReleaseContextAsync().ConfigureAwait(false);
            EfCoreCleanup.ThrowPrimaryFirst(
                null,
                cleanupFailures,
                $"Disposing the Entity Framework Core {_logKind} source failed.");
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private async Task<List<Exception>> ReleaseContextAsync()
    {
        var cleanupFailures = new List<Exception>();
        await _contextRelease.DisposeAsync(async () =>
        {
            var context = _context;
            _context = null;
            if (context is not null)
                await DisposeContextAsync(context, cleanupFailures).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return cleanupFailures;
    }

    private static async Task DisposeContextAsync(TContext context, List<Exception> cleanupFailures)
    {
        try
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }
    }

    private static async ValueTask<(bool HasNext, Exception? Failure)> TryMoveNextAsync(
        IAsyncEnumerator<TResult> enumerator)
    {
        try
        {
            return (await enumerator.MoveNextAsync().ConfigureAwait(false), null);
        }
        catch (Exception exception)
        {
            return (false, exception);
        }
    }

    private static (ProcessingEnvelope<TResult>? Envelope, Exception? Failure) TryCreateEnvelope(TResult payload)
    {
        try
        {
            return (ProcessingEnvelope<TResult>.Create(payload), null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }
}
