#nullable enable

using System.Runtime.ExceptionServices;

namespace SmartPipe.Core;

internal static class PipelineActivator
{
    public static async ValueTask<ActivatedPipelineGraph<TInput, TOutput>> ActivateAsync<TInput, TOutput>(
        PipelineExecutionPlan<TInput, TOutput> plan,
        PipelineActivationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        var ledger = new PipelineActivationLedger();
        try
        {
            var source = await ActivateRequiredAsync(
                    plan.Source,
                    "source",
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
            ledger.Append(CreateLease("source", plan.Source, source));
            if (plan.Source.Initialize)
                await source.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var stages = new ITypedPipelineStage[plan.Stages.Count];
            for (var index = 0; index < plan.Stages.Count; index++)
            {
                var descriptor = plan.Stages[index];
                var activated = await descriptor.ActivateAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                ledger.Append(activated.Lease);
                stages[index] = activated.RuntimeStage;
                if (descriptor.Initialize)
                {
                    await activated.RuntimeStage.InitializeAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            IPipelineSink<TOutput>? sink = null;
            if (plan.Sink is not null)
            {
                sink = await ActivateRequiredAsync(
                        plan.Sink,
                        "sink",
                        context,
                        cancellationToken)
                    .ConfigureAwait(false);
                ledger.Append(CreateLease("sink", plan.Sink, sink));
                if (plan.Sink.Initialize)
                    await sink.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }

            return new()
            {
                Source = source,
                Stages = Array.AsReadOnly(stages),
                Sink = sink,
                Observers = plan.Observers,
                Lifetime = ledger,
            };
        }
        catch (Exception primary)
        {
            var cleanupErrors = await ledger.RollbackAsync().ConfigureAwait(false);
            if (cleanupErrors.Count == 0)
                ExceptionDispatchInfo.Throw(primary);

            throw new PipelineActivationException(
                plan.Key,
                context.RunId,
                primary,
                cleanupErrors);
        }
    }

    private static async ValueTask<T> ActivateRequiredAsync<T>(
        PipelineComponent<T> descriptor,
        string role,
        PipelineActivationContext context,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var instance = await descriptor.Activator(context, cancellationToken).ConfigureAwait(false);
        return instance ?? throw new InvalidOperationException(
            $"The {role} factory for pipeline '{context.PipelineKey}' returned null.");
    }

    private static ActivatedComponentLease CreateLease<T>(
        string role,
        PipelineComponent<T> descriptor,
        T instance)
        where T : class, IAsyncDisposable =>
        new()
        {
            Role = role,
            Ownership = descriptor.Ownership,
            RuntimeOwnedCleanup =
                descriptor.Ownership == PipelineComponentOwnership.RuntimeOwned
                    ? instance.DisposeAsync
                    : null,
        };
}
