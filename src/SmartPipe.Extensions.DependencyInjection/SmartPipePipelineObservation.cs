using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Provides one immutable point-in-time observation of a registered pipeline.</summary>
public sealed record SmartPipePipelineObservation
{
    private PipelineKey _pipelineKey;
    private IReadOnlyList<SmartPipeRunSnapshot> _activeRuns = Array.Empty<SmartPipeRunSnapshot>();

    /// <summary>Gets the exact pipeline key.</summary>
    public required PipelineKey PipelineKey
    {
        get => _pipelineKey;
        init => _pipelineKey = value.IsEmpty
            ? throw new ArgumentException("Pipeline key must be initialized.", nameof(value))
            : value;
    }

    /// <summary>Gets the UTC capture timestamp.</summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Gets a defensive active-run snapshot ordered by start time and run ID.</summary>
    public required IReadOnlyList<SmartPipeRunSnapshot> ActiveRuns
    {
        get => _activeRuns;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _activeRuns = Array.AsReadOnly(value
                .OrderBy(static run => run.StartedAtUtc)
                .ThenBy(static run => run.Identity.RunId)
                .ToArray());
        }
    }

    /// <summary>Gets the latest committed terminal observation, when one exists.</summary>
    public SmartPipeTerminalRunObservation? LatestTerminal { get; init; }
}
