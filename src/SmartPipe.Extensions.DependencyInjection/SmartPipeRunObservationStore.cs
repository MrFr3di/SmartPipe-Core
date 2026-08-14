using System.Collections.Concurrent;
using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

internal sealed class SmartPipeRunObservationStore :
    ISmartPipeRunObservationSource,
    ISmartPipeMutableRunObservationStore
{
    private readonly ISmartPipeRegistry _registry;
    private readonly ISmartPipeRunRegistry _runRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<PipelineKey, TerminalState> _terminalByKey = [];

    internal SmartPipeRunObservationStore(
        ISmartPipeRegistry registry,
        ISmartPipeRunRegistry runRegistry,
        TimeProvider timeProvider)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runRegistry = runRegistry ?? throw new ArgumentNullException(nameof(runRegistry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public SmartPipePipelineObservation Capture(PipelineKey pipelineKey)
    {
        _registry.GetRegistration(pipelineKey);
        var activeRuns = _runRegistry.GetActiveRuns(pipelineKey);
        SmartPipeTerminalRunObservation? terminal = null;
        if (_terminalByKey.TryGetValue(pipelineKey, out var state))
        {
            lock (state.Gate)
            {
                terminal = state.Latest;
            }
        }

        return new SmartPipePipelineObservation
        {
            PipelineKey = pipelineKey,
            CapturedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
            ActiveRuns = activeRuns,
            LatestTerminal = terminal,
        };
    }

    public IReadOnlyList<SmartPipePipelineObservation> CaptureAll() =>
        Array.AsReadOnly(_registry.GetRegistrations()
            .Select(registration => Capture(registration.Key))
            .ToArray());

    public SmartPipeTerminalRunObservation RecordTerminal(SmartPipeTerminalRunCandidate candidate)
    {
        Validate(candidate);
        var key = candidate.Identity.PipelineKey;
        _registry.GetRegistration(key);
        var state = _terminalByKey.GetOrAdd(key, static _ => new TerminalState());
        lock (state.Gate)
        {
            var sequence = checked(state.Sequence + 1);
            var observation = new SmartPipeTerminalRunObservation
            {
                Identity = candidate.Identity,
                InputType = candidate.InputType,
                OutputType = candidate.OutputType,
                Outcome = candidate.Outcome,
                StartedAtUtc = candidate.StartedAtUtc.ToUniversalTime(),
                CompletedAtUtc = candidate.CompletedAtUtc.ToUniversalTime(),
                Metrics = candidate.Metrics,
                InputCapacity = candidate.InputCapacity,
                OutputCapacity = candidate.OutputCapacity,
                Sequence = sequence,
            };
            state.Sequence = sequence;
            state.Latest = observation;
            return observation;
        }
    }

    private static void Validate(SmartPipeTerminalRunCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(candidate.Identity);
        ArgumentNullException.ThrowIfNull(candidate.InputType);
        ArgumentNullException.ThrowIfNull(candidate.OutputType);
        ArgumentNullException.ThrowIfNull(candidate.Metrics);
        if (candidate.Identity.PipelineKey.IsEmpty || candidate.Identity.RunId == Guid.Empty)
        {
            throw new ArgumentException("Run identity must contain an initialized key and non-empty RunId.", nameof(candidate));
        }

        if (!Enum.IsDefined(candidate.Outcome) || candidate.Outcome == SmartPipeRunObservationOutcome.None)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate));
        }

        if (candidate.InputCapacity <= 0 || candidate.OutputCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate));
        }

        if (candidate.CompletedAtUtc < candidate.StartedAtUtc)
        {
            throw new ArgumentException("Terminal timestamp must not precede the run start timestamp.", nameof(candidate));
        }
    }

    private sealed class TerminalState
    {
        internal object Gate { get; } = new();

        internal long Sequence { get; set; }

        internal SmartPipeTerminalRunObservation? Latest { get; set; }
    }
}
