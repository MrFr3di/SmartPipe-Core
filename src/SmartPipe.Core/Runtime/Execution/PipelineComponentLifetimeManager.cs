#nullable enable

using System.Collections.Concurrent;

namespace SmartPipe.Core;

internal sealed class PipelineComponentLifetimeManager<TInput, TOutput>
{
    private readonly IPipelineSource<TInput> _source;
    private readonly IReadOnlyList<ITypedPipelineStage> _stages;
    private readonly IPipelineSink<TOutput>? _sink;
    private readonly ComponentOwnershipOptions _ownershipOptions;
    private readonly LateStageAttemptRegistry _lateAttemptRegistry;
    private readonly ConcurrentDictionary<string, Func<ValueTask>> _deferredStageDisposals = [];
    private int _componentsDisposed;

    public PipelineComponentLifetimeManager(
        IPipelineSource<TInput> source,
        IReadOnlyList<ITypedPipelineStage> stages,
        IPipelineSink<TOutput>? sink,
        ComponentOwnershipOptions ownershipOptions,
        LateStageAttemptRegistry lateAttemptRegistry)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _stages = stages ?? throw new ArgumentNullException(nameof(stages));
        _sink = sink;
        _ownershipOptions = ownershipOptions;
        _lateAttemptRegistry = lateAttemptRegistry ?? throw new ArgumentNullException(nameof(lateAttemptRegistry));
    }

    public async ValueTask InitializeAsync(CancellationToken ct)
    {
        await _source.InitializeAsync(ct).ConfigureAwait(false);
        foreach (var stage in _stages)
            await stage.InitializeAsync(ct).ConfigureAwait(false);

        if (_sink is not null)
            await _sink.InitializeAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<PipelineComponentCleanupResult> DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _componentsDisposed, 1, 0) != 0)
            return new PipelineComponentCleanupResult([], []);

        var lateAttemptErrors = await _lateAttemptRegistry.WaitForAllAsync().ConfigureAwait(false);
        List<Func<ValueTask>> actions = [];

        if (_sink is not null && ShouldDispose(_sink))
            actions.Add(() => _sink.DisposeAsync());

        for (int i = _stages.Count - 1; i >= 0; i--)
        {
            var stage = _stages[i];
            if (_lateAttemptRegistry.HasRunningAttempt(stage.StageId))
            {
                _deferredStageDisposals.TryAdd(
                    stage.StageId,
                    () => stage.DisposeAsync(_ownershipOptions));
                continue;
            }

            actions.Add(() => stage.DisposeAsync(_ownershipOptions));
        }

        if (ShouldDispose(_source))
            actions.Add(() => _source.DisposeAsync());

        var cleanupErrors = await RuntimeCleanup.CollectAsync(actions).ConfigureAwait(false);
        return new PipelineComponentCleanupResult(
            lateAttemptErrors.Concat(cleanupErrors).ToArray(),
            cleanupErrors);
    }

    public async ValueTask<Exception[]> DisposeDeferredStagesAsync()
    {
        if (_deferredStageDisposals.IsEmpty)
            return [];

        List<Exception>? errors = null;
        foreach (var (stageId, dispose) in _deferredStageDisposals.ToArray())
        {
            await _lateAttemptRegistry.WaitForStageAttemptsToCompleteAsync(stageId).ConfigureAwait(false);
            if (!_deferredStageDisposals.TryRemove(stageId, out _))
                continue;

            try
            {
                await dispose().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors ??= [];
                errors.Add(ex);
            }
        }

        return errors?.ToArray() ?? [];
    }

    private bool ShouldDispose(object component)
    {
        if (component is not IPipelineComponentDescriptor descriptor)
            return true;

        return descriptor.Lifetime != PipelineComponentLifetime.SingletonExternal
            || _ownershipOptions.DisposeExternalComponents;
    }
}

internal readonly record struct PipelineComponentCleanupResult(
    Exception[] CompletionErrors,
    Exception[] DisposeErrors);
