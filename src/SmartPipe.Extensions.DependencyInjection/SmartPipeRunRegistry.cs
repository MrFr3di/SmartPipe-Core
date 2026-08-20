using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

internal interface ISmartPipeMutableRunRegistry
{
    IDisposable Register<TInput, TOutput>(
        PipelineRun<TOutput> run,
        DateTimeOffset startedAtUtc);
}

internal sealed class SmartPipeRunRegistry : ISmartPipeRunRegistry, ISmartPipeMutableRunRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(PipelineKey Key, Guid RunId), ActiveRun> _runs = [];

    public IReadOnlyList<SmartPipeRunSnapshot> GetActiveRuns(PipelineKey pipelineKey)
    {
        ThrowIfInvalid(pipelineKey);
        ActiveRun[] active;
        lock (_gate)
        {
            active = _runs.Values
                .Where(run => run.PipelineKey == pipelineKey)
                .ToArray();
        }

        return Array.AsReadOnly(
            active
                .Select(static run => run.GetSnapshot())
                .OrderBy(static snapshot => snapshot.StartedAtUtc)
                .ThenBy(static snapshot => snapshot.Identity.RunId)
                .ToArray());
    }

    public IDisposable Register<TInput, TOutput>(
        PipelineRun<TOutput> run,
        DateTimeOffset startedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(run);
        ThrowIfInvalid(run.PipelineKey);
        if (run.RunId == Guid.Empty)
        {
            throw new ArgumentException("RunId must not be empty.", nameof(run));
        }

        if (run.InputCapacity <= 0 || run.OutputCapacity <= 0)
        {
            throw new ArgumentException(
                "Runtime-created runs must expose positive effective capacities.",
                nameof(run));
        }

        var identity = (run.PipelineKey, run.RunId);
        var active = new ActiveRun<TInput, TOutput>(
            run,
            startedAtUtc.ToUniversalTime());
        lock (_gate)
        {
            if (!_runs.TryAdd(identity, active))
            {
                throw new InvalidOperationException(
                    $"Run '{run.RunId}' for pipeline '{run.PipelineKey.Value}' is already registered.");
            }
        }

        return new RegistrationLease(this, identity, active);
    }

    private void Unregister(
        (PipelineKey Key, Guid RunId) identity,
        ActiveRun expected)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(identity, out var current)
                && ReferenceEquals(current, expected))
            {
                _runs.Remove(identity);
            }
        }
    }

    private static void ThrowIfInvalid(PipelineKey key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("Pipeline key must be initialized.", nameof(key));
        }
    }

    private abstract class ActiveRun
    {
        protected ActiveRun(PipelineKey pipelineKey) => PipelineKey = pipelineKey;

        internal PipelineKey PipelineKey { get; }

        internal abstract SmartPipeRunSnapshot GetSnapshot();
    }

    private sealed class ActiveRun<TInput, TOutput> : ActiveRun
    {
        private readonly PipelineRun<TOutput> _run;
        private readonly DateTimeOffset _startedAtUtc;

        internal ActiveRun(PipelineRun<TOutput> run, DateTimeOffset startedAtUtc)
            : base(run.PipelineKey)
        {
            _run = run;
            _startedAtUtc = startedAtUtc;
        }

        internal override SmartPipeRunSnapshot GetSnapshot() => new()
        {
            Identity = new SmartPipeRunIdentity
            {
                PipelineKey = _run.PipelineKey,
                RunId = _run.RunId,
            },
            InputType = typeof(TInput),
            OutputType = typeof(TOutput),
            StartedAtUtc = _startedAtUtc,
            State = _run.State,
            Metrics = _run.Metrics,
            InputCapacity = _run.InputCapacity,
            OutputCapacity = _run.OutputCapacity,
        };
    }

    private sealed class RegistrationLease : IDisposable
    {
        private readonly SmartPipeRunRegistry _owner;
        private readonly (PipelineKey Key, Guid RunId) _identity;
        private readonly ActiveRun _expected;
        private int _disposed;

        internal RegistrationLease(
            SmartPipeRunRegistry owner,
            (PipelineKey Key, Guid RunId) identity,
            ActiveRun expected)
        {
            _owner = owner;
            _identity = identity;
            _expected = expected;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Unregister(_identity, _expected);
            }
        }
    }
}
