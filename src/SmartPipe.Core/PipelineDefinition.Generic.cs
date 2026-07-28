#nullable enable

using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;

namespace SmartPipe.Core;

/// <summary>Immutable typed pipeline definition.</summary>
public sealed class PipelineDefinition<TInput, TOutput>
{
    private readonly PipelineDefinitionState<TInput, TOutput> _state;
    private readonly PipelineComponent<IPipelineSink<TOutput>>? _sink;
    private readonly Lazy<PipelineExecutionPlan<TInput, TOutput>> _executionPlan;
    private readonly PipelineStartClaim _startClaim;

    internal PipelineDefinition(
        PipelineDefinitionState<TInput, TOutput> state,
        PipelineComponent<IPipelineSink<TOutput>>? sink,
        PipelineStartClaim? startClaim = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        _state = state;
        _sink = sink;
        _startClaim = startClaim ?? new PipelineStartClaim();
        Key = state.Key;
        Stages = CreateMetadata(state.Stages);
        RuntimeOptions = state.RuntimeOptions.Materialize();
        LineageMode = state.LineageMode;
        HasSink = sink is not null;
        IsReusable = PipelineDefinitionCompiler.IsReusable(state, sink);
        _executionPlan = new(
            () => PipelineDefinitionCompiler.Compile(_state, _sink),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Gets the explicit definition key.</summary>
    public PipelineKey Key { get; }

    /// <summary>Gets immutable, resource-free stage metadata.</summary>
    public IReadOnlyList<PipelineStageMetadata> Stages { get; }

    /// <summary>Gets a defensive copy of the runtime options snapshot.</summary>
    public PipelineRuntimeOptions RuntimeOptions { get; }

    /// <summary>Gets the lineage capture mode.</summary>
    public LineageMode LineageMode { get; }

    /// <summary>Gets whether the definition has a terminal sink.</summary>
    public bool HasSink { get; }

    /// <summary>Gets whether the definition can safely activate more than once.</summary>
    public bool IsReusable { get; }

    internal static PipelineDefinition<TInput, TOutput> Create(
        PipelineDefinitionState<TInput, TOutput> state,
        PipelineComponent<IPipelineSink<TOutput>>? sink,
        PipelineStartClaim? startClaim = null)
    {
        PipelineDefinitionCompiler.Validate(state, sink);
        return new(state, sink, startClaim);
    }

    internal PipelineExecutionPlan<TInput, TOutput> GetExecutionPlan() =>
        _executionPlan.Value;

    internal async ValueTask<ActivatedPipelineGraph<TInput, TOutput>> ActivateAsync(
        PipelineActivationContext context,
        CancellationToken cancellationToken)
    {
        var plan = PrepareActivation(context, cancellationToken);
        return await PipelineActivator.ActivateAsync(plan, context, cancellationToken)
            .ConfigureAwait(false);
    }

#pragma warning disable RS0026 // The final SP220-02 contract freezes both optional-token overloads.
    /// <summary>Starts a run using a generated run identifier and no activation services.</summary>
    /// <param name="cancellationToken">Cancellation token linked to startup and the run.</param>
    /// <returns>The ready runtime-created run.</returns>
    public Task<PipelineRun<TOutput>> StartAsync(
        CancellationToken cancellationToken = default) =>
        StartAsync(
            new PipelineActivationContext(Key, Guid.NewGuid()),
            cancellationToken);

    /// <summary>Starts a run with an explicit activation context.</summary>
    /// <param name="context">Per-run identity and borrowed activation dependencies.</param>
    /// <param name="cancellationToken">Cancellation token linked to startup and the run.</param>
    /// <returns>The run after initialization, Running state, and started-event acceptance.</returns>
    public async Task<PipelineRun<TOutput>> StartAsync(
        PipelineActivationContext context,
        CancellationToken cancellationToken = default)
    {
        var operation = StartDeferred(context, cancellationToken);
        try
        {
            await operation.Ready.ConfigureAwait(false);
            return operation.Run;
        }
        catch (Exception readyError)
        {
            var failure = ExceptionDispatchInfo.Capture(readyError);
            try
            {
                await operation.Completion.ConfigureAwait(false);
            }
            catch (Exception completionError)
            {
                failure = ExceptionDispatchInfo.Capture(completionError);
            }

            try
            {
                await operation.Run.DisposeAsync().ConfigureAwait(false);
            }
            catch when (operation.Completion.IsFaulted || operation.Completion.IsCanceled)
            {
                // The owned completion already carries activation/runtime cleanup failures.
            }

            failure.Throw();
            throw;
        }
    }
#pragma warning restore RS0026

    internal PipelineStartOperation<TOutput> StartDeferred(
        PipelineActivationContext context,
        CancellationToken cancellationToken)
    {
        var plan = PrepareActivation(context, cancellationToken);
        return PipelineStartOperation<TOutput>.Start(plan, context, cancellationToken);
    }

    private PipelineExecutionPlan<TInput, TOutput> PrepareActivation(
        PipelineActivationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.PipelineKey != Key)
        {
            throw new ArgumentException(
                $"Activation context key '{context.PipelineKey}' does not match definition key '{Key}'.",
                nameof(context));
        }

        if (context.RunId == Guid.Empty)
            throw new ArgumentException("RunId must not be empty.", nameof(context));

        cancellationToken.ThrowIfCancellationRequested();
        var plan = GetExecutionPlan();
        if (plan.RequiresServices && context.Services is null)
        {
            throw new InvalidOperationException(
                $"Pipeline '{Key}' requires activation services.");
        }

        if (!plan.IsReusable && !_startClaim.TryClaim())
        {
            throw new InvalidOperationException(
                $"Pipeline definition '{Key}' is single-use and has already been activated.");
        }

        return plan;
    }

    private static ReadOnlyCollection<PipelineStageMetadata> CreateMetadata(
        IPipelineStageDescriptor[] stages)
    {
        var metadata = new PipelineStageMetadata[stages.Length];
        for (var index = 0; index < stages.Length; index++)
            metadata[index] = stages[index].Metadata;

        return Array.AsReadOnly(metadata);
    }
}

internal sealed class PipelineStartClaim
{
    private int _claimed;

    public bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
}
