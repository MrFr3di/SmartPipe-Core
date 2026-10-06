namespace SmartPipe.Extensions.DependencyInjection;

internal interface ISmartPipeMutableRunObservationStore
{
    SmartPipeTerminalRunObservation RecordTerminal(SmartPipeTerminalRunCandidate candidate);
}
