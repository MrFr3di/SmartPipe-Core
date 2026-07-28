#nullable enable

namespace SmartPipe.Core;

internal interface IPipelineStageDescriptor
{
    PipelineStageKey Key { get; }

    string Name { get; }

    Type InputType { get; }

    Type OutputType { get; }

    StageFailureOptionsSnapshot FailureOptions { get; }

    PipelineStageMetadata Metadata { get; }

    bool IsPerRun { get; }

    bool Initialize { get; }

    bool RequiresServices { get; }

    bool HasDeadLetterOptions { get; }

    ValueTask<ActivatedStage> ActivateAsync(
        PipelineActivationContext context,
        CancellationToken cancellationToken);
}

internal sealed class PipelineStageDescriptor<TInput, TOutput> : IPipelineStageDescriptor
{
    public PipelineStageDescriptor(
        PipelineStageKey key,
        PipelineComponent<IPipelineTransformer<TInput, TOutput>> transformer,
        StageFailureOptionsSnapshot failureOptions,
        StageDeadLetterOptions<TInput>? deadLetterOptions,
        string name)
    {
        PipelineStageKeyGuard.ThrowIfInvalid(key, nameof(key));
        ArgumentNullException.ThrowIfNull(transformer);
        ArgumentNullException.ThrowIfNull(failureOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        failureOptions.Validate();

        Key = key;
        Transformer = transformer;
        FailureOptions = failureOptions;
        DeadLetterOptions = deadLetterOptions;
        Name = name;
        Metadata = new(key, name, typeof(TInput), typeof(TOutput), failureOptions);
    }

    public PipelineStageKey Key { get; }

    public string Name { get; }

    public Type InputType => typeof(TInput);

    public Type OutputType => typeof(TOutput);

    public StageFailureOptionsSnapshot FailureOptions { get; }

    public PipelineStageMetadata Metadata { get; }

    public bool IsPerRun => Transformer.IsPerRun;

    public bool Initialize => Transformer.Initialize;

    public bool RequiresServices =>
        Transformer.Ownership == PipelineComponentOwnership.ScopeOwned;

    public bool HasDeadLetterOptions => DeadLetterOptions is not null;

    internal PipelineComponent<IPipelineTransformer<TInput, TOutput>> Transformer { get; }

    internal StageDeadLetterOptions<TInput>? DeadLetterOptions { get; }

    public async ValueTask<ActivatedStage> ActivateAsync(
        PipelineActivationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transformer = await Transformer.Activator(context, cancellationToken)
            .ConfigureAwait(false);
        if (transformer is null)
        {
            throw new InvalidOperationException(
                $"The stage '{Key.Value}' factory for pipeline '{context.PipelineKey}' returned null.");
        }

        var runtimeStage = new TypedPipelineStage<TInput, TOutput>(
            transformer,
            Key,
            Name,
            FailureOptions.Materialize(),
            DeadLetterOptions);
        return new()
        {
            RuntimeStage = runtimeStage,
            Lease = new()
            {
                Role = $"stage '{Key.Value}'",
                Ownership = Transformer.Ownership,
                StageKey = Key,
                RuntimeOwnedCleanup =
                    Transformer.Ownership == PipelineComponentOwnership.RuntimeOwned
                        ? transformer.DisposeAsync
                        : null,
            },
        };
    }
}
