using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection.Tests;

public sealed class ObservationContractTests
{
    [Theory]
    [InlineData(SmartPipeRunObservationOutcome.None, 0)]
    [InlineData(SmartPipeRunObservationOutcome.Completed, 1)]
    [InlineData(SmartPipeRunObservationOutcome.Cancelled, 2)]
    [InlineData(SmartPipeRunObservationOutcome.Aborted, 3)]
    [InlineData(SmartPipeRunObservationOutcome.Faulted, 4)]
    [InlineData(SmartPipeRunObservationOutcome.ActivationFailed, 5)]
    public void Outcome_UsesStableExplicitValues(
        SmartPipeRunObservationOutcome outcome,
        int value) => Assert.Equal(value, (int)outcome);

    [Fact]
    public void PipelineObservation_DefensivelyCopiesAndOrdersActiveRuns()
    {
        var key = new PipelineKey("orders");
        var laterId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var earlierId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var started = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var runs = new List<SmartPipeRunSnapshot>
        {
            Run(key, laterId, started),
            Run(key, earlierId, started),
        };

        var observation = new SmartPipePipelineObservation
        {
            PipelineKey = key,
            CapturedAtUtc = started.AddMinutes(1),
            ActiveRuns = runs,
        };
        runs.Clear();

        Assert.Equal([earlierId, laterId], observation.ActiveRuns.Select(run => run.Identity.RunId));
    }

    [Fact]
    public void TerminalObservation_RejectsInvalidBoundaryValues()
    {
        var valid = ValidTerminal();

        Assert.Throws<ArgumentException>(() => _ = valid with
        {
            Identity = new SmartPipeRunIdentity { PipelineKey = default, RunId = Guid.NewGuid() },
        });
        Assert.Throws<ArgumentException>(() => _ = valid with
        {
            Identity = new SmartPipeRunIdentity { PipelineKey = new PipelineKey("orders"), RunId = Guid.Empty },
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = valid with { Outcome = SmartPipeRunObservationOutcome.None });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = valid with { Outcome = (SmartPipeRunObservationOutcome)999 });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = valid with { InputCapacity = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = valid with { OutputCapacity = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = valid with { Sequence = 0 });
        Assert.Throws<ArgumentNullException>(() => _ = valid with { InputType = null! });
        Assert.Throws<ArgumentNullException>(() => _ = valid with { OutputType = null! });
        Assert.Throws<ArgumentNullException>(() => _ = valid with { Metrics = null! });
    }

    [Fact]
    public void PipelineObservation_RejectsInvalidBoundaryValues()
    {
        Assert.Throws<ArgumentException>(() => new SmartPipePipelineObservation
        {
            PipelineKey = default,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ActiveRuns = [],
        });
        Assert.Throws<ArgumentNullException>(() => new SmartPipePipelineObservation
        {
            PipelineKey = new PipelineKey("orders"),
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ActiveRuns = null!,
        });
    }

    [Fact]
    public void PublicObservationValues_DoNotRetainRuntimeGraphs()
    {
        Type[] forbidden = [typeof(Exception), typeof(Delegate), typeof(IServiceProvider), typeof(PipelineRun<>)];
        Type[] observations = [typeof(SmartPipeTerminalRunObservation), typeof(SmartPipePipelineObservation)];

        foreach (var property in observations.SelectMany(type => type.GetProperties()))
        {
            Assert.DoesNotContain(forbidden, type =>
                type.IsGenericTypeDefinition
                    ? property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == type
                    : type.IsAssignableFrom(property.PropertyType));
        }
    }

    [Fact]
    public void ObservationValueGraph_DoesNotRetainRuntimeObjects()
    {
        Type[] forbiddenDefinitions =
        [
            typeof(PipelineRun<>),
            typeof(IPipelineSource<>),
            typeof(IPipelineTransformer<,>),
            typeof(IPipelineSink<>),
        ];
        Type[] roots =
        [
            typeof(SmartPipePipelineObservation),
            typeof(SmartPipeTerminalRunObservation),
            typeof(SmartPipeRunSnapshot),
            typeof(SmartPipeRunIdentity),
            typeof(SmartPipeTerminalRunCandidate),
        ];
        var visited = new HashSet<Type>();
        var pending = new Stack<Type>(roots);

        while (pending.TryPop(out var type))
        {
            if (!visited.Add(type))
            {
                continue;
            }

            Assert.False(
                IsForbidden(type, forbiddenDefinitions),
                $"Observation graph retains forbidden type '{type}'.");

            if (type == typeof(Type) || type == typeof(string) || type.IsValueType)
            {
                continue;
            }

            if (type.IsArray)
            {
                pending.Push(type.GetElementType()!);
                continue;
            }

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    pending.Push(argument);
                }
            }

            foreach (var property in type.GetProperties())
            {
                Assert.False(
                    IsForbidden(property.PropertyType, forbiddenDefinitions),
                    $"'{type}.{property.Name}' retains forbidden type '{property.PropertyType}'.");
                pending.Push(property.PropertyType);
            }
        }

        static bool IsForbidden(Type type, IReadOnlyCollection<Type> forbiddenDefinitions) =>
            typeof(Exception).IsAssignableFrom(type)
            || typeof(Delegate).IsAssignableFrom(type)
            || typeof(IServiceProvider).IsAssignableFrom(type)
            || typeof(IServiceScope).IsAssignableFrom(type)
            || (type.IsGenericType && forbiddenDefinitions.Contains(type.GetGenericTypeDefinition()));
    }

    private static SmartPipeTerminalRunObservation ValidTerminal() => new()
    {
        Identity = new SmartPipeRunIdentity
        {
            PipelineKey = new PipelineKey("orders"),
            RunId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        },
        InputType = typeof(int),
        OutputType = typeof(string),
        Outcome = SmartPipeRunObservationOutcome.Completed,
        StartedAtUtc = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero),
        CompletedAtUtc = new DateTimeOffset(2026, 8, 13, 12, 1, 0, TimeSpan.Zero),
        Metrics = SmartPipeMetricsSnapshot.Empty,
        InputCapacity = 8,
        OutputCapacity = 4,
        Sequence = 1,
    };

    private static SmartPipeRunSnapshot Run(PipelineKey key, Guid runId, DateTimeOffset started) => new()
    {
        Identity = new SmartPipeRunIdentity { PipelineKey = key, RunId = runId },
        InputType = typeof(int),
        OutputType = typeof(string),
        StartedAtUtc = started,
        State = PipelineRunState.Running,
        Metrics = SmartPipeMetricsSnapshot.Empty,
        InputCapacity = 8,
        OutputCapacity = 4,
    };
}
