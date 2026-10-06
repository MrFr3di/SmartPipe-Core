using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>Combines transforms sequentially and owns their asynchronous lifecycle.</summary>
public class CompositeTransform<T> : IPipelineTransformer<T, T>
{
    private readonly object _sync = new();
    private readonly IPipelineTransformer<T, T>[] _transforms;
    private readonly List<IPipelineTransformer<T, T>> _acquired = [];
    private Task? _initializeTask;
    private Task? _disposeTask;
    private bool _initialized;

    /// <summary>Initializes a composite from transforms in acquisition order.</summary>
    public CompositeTransform(params IPipelineTransformer<T, T>[] transforms)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        if (Array.Exists(transforms, static transform => transform is null))
            throw new ArgumentException("Transforms cannot contain null elements.", nameof(transforms));

        _transforms = [.. transforms];
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _initializeTask ??= InitializeCoreAsync(ct);
            return new ValueTask(_initializeTask);
        }
    }

    /// <inheritdoc />
    public async ValueTask<StageResult<T>> TransformAsync(
        ProcessingEnvelope<T> envelope,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (!_initialized)
                throw new InvalidOperationException("The composite must be initialized before transforming items.");
        }

        var current = envelope;
        foreach (IPipelineTransformer<T, T> transform in _transforms)
        {
            StageResult<T> result = await transform.TransformAsync(current, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
                return result;

            current = current with { Payload = result.Value! };
        }

        return StageResult<T>.Success(current.Payload);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposeTask ??= DisposeCoreAsync(_initializeTask);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        try
        {
            foreach (IPipelineTransformer<T, T> transform in _transforms)
            {
                _acquired.Add(transform);
                await transform.InitializeAsync(ct).ConfigureAwait(false);
            }

            lock (_sync)
                _initialized = true;
        }
        catch (Exception primary)
        {
            List<Exception> errors = [primary];
            await CleanupAsync(errors).ConfigureAwait(false);
            if (errors.Count == 1)
                throw;

            throw new AggregateException(errors);
        }
    }

    private async Task DisposeCoreAsync(Task? initializeTask)
    {
        if (initializeTask is not null)
        {
            try
            {
                await initializeTask.ConfigureAwait(false);
            }
            catch
            {
                // Initialization reports its own primary failure and performs rollback.
            }
        }

        var errors = new List<Exception>();
        await CleanupAsync(errors).ConfigureAwait(false);
        if (errors.Count > 0)
            throw new AggregateException(errors);
    }

    private async Task CleanupAsync(List<Exception> errors)
    {
        for (int i = _acquired.Count - 1; i >= 0; i--)
        {
            try
            {
                await _acquired[i].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        _acquired.Clear();
    }
}
