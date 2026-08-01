using SmartPipe.Core;

namespace SmartPipe.Extensions.Hosting;

internal sealed class HostedRegistrationReservation
{
    internal HostedRegistrationReservation(
        SmartPipeHostedRegistrationStore store,
        PipelineKey key,
        Type inputType,
        Type outputType,
        int registrationOrder)
    {
        Store = store;
        Key = key;
        InputType = inputType;
        OutputType = outputType;
        RegistrationOrder = registrationOrder;
    }

    internal SmartPipeHostedRegistrationStore Store { get; }

    internal PipelineKey Key { get; }

    internal Type InputType { get; }

    internal Type OutputType { get; }

    internal int RegistrationOrder { get; }

    internal HostedPipelineDescriptor? Descriptor { get; set; }

    internal bool IsCompleted { get; set; }
}
