#nullable enable

using Polly;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Polly;

#pragma warning disable RS0026 // Pipeline-factory and pipeline-instance overloads are the frozen SP220-14 contract.
/// <summary>Creates runtime-owned Polly decorator components for Core's stage-keyed transform APIs.</summary>
/// <remarks>
/// Pass the returned component to <c>Transform(stageKey, component)</c> on an initial or typed
/// definition builder. Core owns, initializes, and disposes the decorator; the decorator initializes the
/// inner transform once and disposes it only when <see cref="PollyInnerTransformOwnership.Owned"/>.
/// </remarks>
public static class PollyPipelineComponents
{
    /// <summary>Creates a decorator component whose pipeline is resolved for each activation.</summary>
    /// <typeparam name="TInput">Input payload type.</typeparam>
    /// <typeparam name="TOutput">Output payload type.</typeparam>
    /// <param name="innerFactory">Acquires the inner transform for one activation. It must not initialize it.</param>
    /// <param name="pipelineFactory">
    /// Resolves the application-owned pipeline for one activation. It runs before <paramref name="innerFactory"/>.
    /// </param>
    /// <param name="innerOwnership">Whether the decorator disposes the acquired inner transform.</param>
    /// <param name="options">Optional settings, snapshotted when the component is created.</param>
    /// <returns>A runtime-owned transform component.</returns>
    /// <exception cref="ArgumentNullException">A factory is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="innerOwnership"/> is not defined.</exception>
    /// <exception cref="ArgumentException">The operation key is invalid.</exception>
    public static PipelineComponent<IPipelineTransformer<TInput, TOutput>> Decorate<TInput, TOutput>(
        Func<PipelineActivationContext, CancellationToken, ValueTask<IPipelineTransformer<TInput, TOutput>>> innerFactory,
        Func<PipelineActivationContext, ResiliencePipeline<StageResult<TOutput>>> pipelineFactory,
        PollyInnerTransformOwnership innerOwnership,
        PollyTransformDecoratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(innerFactory);
        ArgumentNullException.ThrowIfNull(pipelineFactory);
        return Create(innerFactory, pipelineFactory, innerOwnership, options);
    }

    /// <summary>Creates a decorator component that shares one application-owned pipeline.</summary>
    /// <typeparam name="TInput">Input payload type.</typeparam>
    /// <typeparam name="TOutput">Output payload type.</typeparam>
    /// <param name="innerFactory">Acquires the inner transform for one activation. It must not initialize it.</param>
    /// <param name="pipeline">The application-owned pipeline shared by every activation. It is never disposed.</param>
    /// <param name="innerOwnership">Whether the decorator disposes the acquired inner transform.</param>
    /// <param name="options">Optional settings, snapshotted when the component is created.</param>
    /// <returns>A runtime-owned transform component.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="innerFactory"/> or <paramref name="pipeline"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="innerOwnership"/> is not defined.</exception>
    /// <exception cref="ArgumentException">The operation key is invalid.</exception>
    public static PipelineComponent<IPipelineTransformer<TInput, TOutput>> Decorate<TInput, TOutput>(
        Func<PipelineActivationContext, CancellationToken, ValueTask<IPipelineTransformer<TInput, TOutput>>> innerFactory,
        ResiliencePipeline<StageResult<TOutput>> pipeline,
        PollyInnerTransformOwnership innerOwnership,
        PollyTransformDecoratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(innerFactory);
        ArgumentNullException.ThrowIfNull(pipeline);
        return Create(innerFactory, _ => pipeline, innerOwnership, options);
    }

    private static PipelineComponent<IPipelineTransformer<TInput, TOutput>> Create<TInput, TOutput>(
        Func<PipelineActivationContext, CancellationToken, ValueTask<IPipelineTransformer<TInput, TOutput>>> innerFactory,
        Func<PipelineActivationContext, ResiliencePipeline<StageResult<TOutput>>> pipelineFactory,
        PollyInnerTransformOwnership innerOwnership,
        PollyTransformDecoratorOptions? options)
    {
        PollyTransformDecoratorSettings.ThrowIfUndefined(innerOwnership, nameof(innerOwnership));
        var settings = PollyTransformDecoratorSettings.Create(options);

        return PipelineComponent.RuntimeOwned<IPipelineTransformer<TInput, TOutput>>(
            (context, cancellationToken) => ActivateAsync(
                innerFactory,
                pipelineFactory,
                innerOwnership,
                settings,
                context,
                cancellationToken));
    }

    private static async ValueTask<IPipelineTransformer<TInput, TOutput>> ActivateAsync<TInput, TOutput>(
        Func<PipelineActivationContext, CancellationToken, ValueTask<IPipelineTransformer<TInput, TOutput>>> innerFactory,
        Func<PipelineActivationContext, ResiliencePipeline<StageResult<TOutput>>> pipelineFactory,
        PollyInnerTransformOwnership innerOwnership,
        PollyTransformDecoratorSettings settings,
        PipelineActivationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pipeline = pipelineFactory(context)
            ?? throw new InvalidOperationException(
                $"The Polly pipeline factory for pipeline '{context.PipelineKey}' returned null.");
        var inner = await innerFactory(context, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"The inner transform factory for pipeline '{context.PipelineKey}' returned null.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new PollyTransformDecorator<TInput, TOutput>(inner, pipeline, innerOwnership, settings);
        }
        catch (Exception primary) when (innerOwnership == PollyInnerTransformOwnership.Owned)
        {
            await DisposeAfterFailedActivationAsync(inner, primary).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask DisposeAfterFailedActivationAsync<TInput, TOutput>(
        IPipelineTransformer<TInput, TOutput> inner,
        Exception primary)
    {
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanup)
        {
            throw new AggregateException(
                "Disposing the owned inner transform failed after activation failed. The activation failure is first.",
                primary,
                cleanup);
        }
    }
}
#pragma warning restore RS0026
