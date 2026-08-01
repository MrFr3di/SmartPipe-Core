using SmartPipe.Core;

namespace SmartPipe.Extensions.Hosting;

internal sealed record HostedPipelineDescriptor
{
    internal required PipelineKey Key { get; init; }

    internal required Type InputType { get; init; }

    internal required Type OutputType { get; init; }

    internal required int Order { get; init; }

    internal required int RegistrationOrder { get; init; }

    internal required TimeSpan DrainTimeout { get; init; }

    internal required SmartPipeHostedPipelineFailureBehavior FailureBehavior { get; init; }

    internal required SmartPipeHostedCompletionBehavior CompletionBehavior { get; init; }
}
