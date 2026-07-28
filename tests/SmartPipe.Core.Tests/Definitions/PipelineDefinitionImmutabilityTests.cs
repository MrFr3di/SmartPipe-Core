#pragma warning disable CS0618 // Compatibility aliases are part of the snapshot contract.

using System.Reflection;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineDefinitionImmutabilityTests
{
    [Fact]
    public void PipelineRuntimeOptionsSnapshot_CopiesEveryReadableOption()
    {
        var clock = new TestClock();
        var options = new PipelineRuntimeOptions
        {
            MaxConcurrency = 8,
            InputCapacity = 17,
            InputFullMode = BoundedChannelFullMode.Wait,
            OutputCapacity = 19,
            OutputFullMode = BoundedChannelFullMode.DropOldest,
            OutputMode = PipelineOutputMode.FailuresOnlyWhenSinkAttached,
            MaxDegreeOfParallelism = 8,
            OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
            OrderingMode = PipelineOrderingMode.Unordered,
            ObserverDispatch = new ObserverDispatchOptions
            {
                Mode = ObserverDispatchMode.BufferedBestEffort,
                Capacity = 23,
                FullMode = BoundedChannelFullMode.DropWrite,
                FailureMode = ObserverFailureMode.Ignore,
                FlushOnCompletion = false,
                BestEffortWriteTimeout = TimeSpan.FromMilliseconds(29),
                EmitDroppedObserverEvents = false,
            },
            AdaptiveParallelism = new AdaptiveParallelismOptions
            {
                Enabled = true,
                MinConcurrency = 2,
                MaxConcurrency = 8,
                InitialConcurrency = 3,
                TargetLatency = TimeSpan.FromMilliseconds(31),
                DeadZone = TimeSpan.FromMilliseconds(5),
                EvaluationInterval = TimeSpan.FromMilliseconds(37),
                AdjustmentCooldown = TimeSpan.FromMilliseconds(41),
                MaxAdjustmentStep = 2,
                FailurePressureThreshold = 0.25,
                MinimumFailureSamples = 7,
                MinSmoothingFactor = 0.4,
            },
            Clock = clock,
        };

        var snapshot = PipelineRuntimeOptionsSnapshot.Create(options);

        snapshot.MaxConcurrency.Should().Be(8);
        snapshot.InputCapacity.Should().Be(17);
        snapshot.InputFullMode.Should().Be(BoundedChannelFullMode.Wait);
        snapshot.OutputCapacity.Should().Be(19);
        snapshot.OutputFullMode.Should().Be(BoundedChannelFullMode.DropOldest);
        snapshot.OutputMode.Should().Be(PipelineOutputMode.FailuresOnlyWhenSinkAttached);
        snapshot.MaxDegreeOfParallelism.Should().Be(8);
        snapshot.OutputPolicy.Should().Be(PipelineOutputPolicy.SuppressSuccessWhenSinkAttached);
        snapshot.OrderingMode.Should().Be(PipelineOrderingMode.Unordered);
        snapshot.Clock.Should().BeSameAs(clock);
        snapshot.IsOutputModeConfigured.Should().BeTrue();
        snapshot.IsOutputPolicyConfigured.Should().BeTrue();
        snapshot.IsClockConfigured.Should().BeTrue();

        snapshot.ObserverDispatch.Mode.Should().Be(ObserverDispatchMode.BufferedBestEffort);
        snapshot.ObserverDispatch.Capacity.Should().Be(23);
        snapshot.ObserverDispatch.FullMode.Should().Be(BoundedChannelFullMode.DropWrite);
        snapshot.ObserverDispatch.FailureMode.Should().Be(ObserverFailureMode.Ignore);
        snapshot.ObserverDispatch.FlushOnCompletion.Should().BeFalse();
        snapshot.ObserverDispatch.BestEffortWriteTimeout.Should().Be(TimeSpan.FromMilliseconds(29));
        snapshot.ObserverDispatch.EmitDroppedObserverEvents.Should().BeFalse();

        snapshot.AdaptiveParallelism.Enabled.Should().BeTrue();
        snapshot.AdaptiveParallelism.MinConcurrency.Should().Be(2);
        snapshot.AdaptiveParallelism.MaxConcurrency.Should().Be(8);
        snapshot.AdaptiveParallelism.InitialConcurrency.Should().Be(3);
        snapshot.AdaptiveParallelism.TargetLatency.Should().Be(TimeSpan.FromMilliseconds(31));
        snapshot.AdaptiveParallelism.DeadZone.Should().Be(TimeSpan.FromMilliseconds(5));
        snapshot.AdaptiveParallelism.EvaluationInterval.Should().Be(TimeSpan.FromMilliseconds(37));
        snapshot.AdaptiveParallelism.SampleInterval.Should().Be(TimeSpan.FromMilliseconds(37));
        snapshot.AdaptiveParallelism.AdjustmentCooldown.Should().Be(TimeSpan.FromMilliseconds(41));
        snapshot.AdaptiveParallelism.Cooldown.Should().Be(TimeSpan.FromMilliseconds(41));
        snapshot.AdaptiveParallelism.MaxAdjustmentStep.Should().Be(2);
        snapshot.AdaptiveParallelism.FailurePressureThreshold.Should().Be(0.25);
        snapshot.AdaptiveParallelism.MinimumFailureSamples.Should().Be(7);
        snapshot.AdaptiveParallelism.MinSmoothingFactor.Should().Be(0.4);
    }

    [Fact]
    public void StageFailureSnapshot_CopiesNestedPoliciesAndDelegates()
    {
        Predicate<SmartPipeError> retryOn = error => error.Type == ErrorType.Permanent;
        Action<ProcessingEnvelope<object>, SmartPipeError, int> onRetry = (_, _, _) => { };
        Func<Exception, SmartPipeError> classifier = exception =>
            new SmartPipeError(exception.Message, ErrorType.Permanent, "test");
        var retry = new RetryPolicy(
            4,
            TimeSpan.FromMilliseconds(11),
            TimeSpan.FromMilliseconds(43),
            BackoffStrategy.Linear,
            retryOn,
            onRetry);
        var timeout = new TimeoutPolicy
        {
            AttemptTimeout = TimeSpan.FromMilliseconds(47),
            StageTimeout = TimeSpan.FromMilliseconds(53),
            RetryMode = TimeoutRetryMode.DetachAndRetryIdempotent,
            CancellationGracePeriod = TimeSpan.FromMilliseconds(59),
            LateAttemptFinalizationTimeout = TimeSpan.FromMilliseconds(61),
        };
        var circuit = new CircuitBreakerPolicy
        {
            FailureThreshold = 3,
            BreakDuration = TimeSpan.FromMilliseconds(67),
            EvaluationMode = CircuitBreakerEvaluationMode.FailureRatio,
            FailureRatio = 0.3,
            SamplingDuration = TimeSpan.FromMilliseconds(71),
            MinimumThroughput = 5,
            MaxHalfOpenRequests = 2,
        };
        var options = new StageFailureOptions
        {
            Retry = retry,
            Timeout = timeout,
            CircuitBreaker = circuit,
            ExceptionClassifier = classifier,
            OnPermanentFailure = FailureAction.Skip,
            OnRetryExhausted = FailureAction.DeadLetter,
        };

        var snapshot = StageFailureOptionsSnapshot.Create(options);

        snapshot.Retry.Should().NotBeSameAs(retry);
        snapshot.Retry!.MaxRetries.Should().Be(4);
        snapshot.Retry.Delay.Should().Be(TimeSpan.FromMilliseconds(11));
        snapshot.Retry.MaxDelay.Should().Be(TimeSpan.FromMilliseconds(43));
        snapshot.Retry.Strategy.Should().Be(BackoffStrategy.Linear);
        snapshot.Retry.RetryOn.Should().BeSameAs(retryOn);
        snapshot.Retry.OnRetry.Should().BeSameAs(onRetry);
        snapshot.Timeout.Should().NotBeSameAs(timeout);
        snapshot.Timeout.Should().BeEquivalentTo(timeout);
        snapshot.CircuitBreaker.Should().NotBeSameAs(circuit);
        snapshot.CircuitBreaker.Should().BeEquivalentTo(circuit);
        snapshot.ExceptionClassifier.Should().BeSameAs(classifier);
        snapshot.OnPermanentFailure.Should().Be(FailureAction.Skip);
        snapshot.OnRetryExhausted.Should().Be(FailureAction.DeadLetter);
    }

    [Fact]
    public void SnapshotFactory_ValidatesBeforeCopy()
    {
        var options = new PipelineRuntimeOptions { InputCapacity = 0 };

        var act = () => PipelineRuntimeOptionsSnapshot.Create(options);

        act.Should().ThrowExactly<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be(nameof(PipelineRuntimeOptions.InputCapacity));
    }

    [Fact]
    public void StageFailureSnapshot_ValidatesBeforeCopy()
    {
        var options = new StageFailureOptions
        {
            CircuitBreaker = new CircuitBreakerPolicy { FailureThreshold = 0 },
        };

        var act = () => StageFailureOptionsSnapshot.Create(options);

        act.Should().ThrowExactly<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be(nameof(CircuitBreakerPolicy.FailureThreshold));
    }

    [Fact]
    public void SnapshotMapping_GuardsEveryReadablePublicOptionProperty()
    {
        AssertEveryPropertyMapped<PipelineRuntimeOptions, PipelineRuntimeOptionsSnapshot>();
        AssertEveryPropertyMapped<ObserverDispatchOptions, ObserverDispatchOptionsSnapshot>();
        AssertEveryPropertyMapped<AdaptiveParallelismOptions, AdaptiveParallelismOptionsSnapshot>();
        AssertEveryPropertyMapped<StageFailureOptions, StageFailureOptionsSnapshot>();
    }

    [Fact]
    public void PipelineRuntimeOptionsSnapshot_DefaultClock_IsNotConfigured()
    {
        var snapshot = PipelineRuntimeOptionsSnapshot.Create(new PipelineRuntimeOptions());

        snapshot.IsClockConfigured.Should().BeFalse();
        snapshot.Clock.Should().BeSameAs(SystemPipelineClock.Instance);
    }

    [Fact]
    public void PipelineRuntimeOptionsSnapshot_ExplicitClock_PreservesReference()
    {
        var clock = new TestClock();

        var snapshot = PipelineRuntimeOptionsSnapshot.Create(
            new PipelineRuntimeOptions { Clock = clock });

        snapshot.IsClockConfigured.Should().BeTrue();
        snapshot.Clock.Should().BeSameAs(clock);
    }

    [Fact]
    public void ExplicitContextTimeProvider_Wins()
    {
        var provider = new FakeTimeProvider();
        var snapshot = PipelineRuntimeOptionsSnapshot.Create(
            new PipelineRuntimeOptions { Clock = new TestClock() });
        var context = new PipelineActivationContext(
            new PipelineKey("orders"),
            Guid.NewGuid(),
            timeProvider: provider);

        var resolved = snapshot.ResolveClock(context);

        resolved.Should().BeOfType<TimeProviderPipelineClock>()
            .Which.TimeProvider.Should().BeSameAs(provider);
    }

    [Fact]
    public void ExplicitLegacyClock_IsPreservedWithoutContextOverride()
    {
        var clock = new TestClock();
        var snapshot = PipelineRuntimeOptionsSnapshot.Create(
            new PipelineRuntimeOptions { Clock = clock });
        var context = new PipelineActivationContext(new PipelineKey("orders"), Guid.NewGuid());

        snapshot.ResolveClock(context).Should().BeSameAs(clock);
    }

    [Fact]
    public void DefaultClock_UsesSystem()
    {
        var snapshot = PipelineRuntimeOptionsSnapshot.Create(new PipelineRuntimeOptions());
        var context = new PipelineActivationContext(new PipelineKey("orders"), Guid.NewGuid());

        snapshot.ResolveClock(context).Should().BeSameAs(SystemPipelineClock.Instance);
    }

    [Fact]
    public void GenericDefinition_StagesAreReadOnlyAndCannotBeMutated()
    {
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                    ValueTask.FromResult<IPipelineSource<int>>(
                        PipelineSource.FromAsyncEnumerable(EmptyItems()))))
            .Transform(
                new PipelineStageKey("normalize"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                    ValueTask.FromResult<IPipelineTransformer<int, int>>(
                        PipelineTransformer.FromFunc<int, int>(
                            (value, _) => ValueTask.FromResult(value)))))
            .Build();

        var stages = definition.Stages as IList<PipelineStageMetadata>;

        definition.Stages.Should().NotBeAssignableTo<PipelineStageMetadata[]>();
        stages.Should().NotBeNull();
        var act = () => stages!.RemoveAt(0);
        act.Should().Throw<NotSupportedException>();
    }

    private static async IAsyncEnumerable<int> EmptyItems()
    {
        yield break;
    }

    private static void AssertEveryPropertyMapped<TSource, TSnapshot>()
    {
        var sourceProperties = typeof(TSource)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead)
            .Select(property => property.Name);
        var snapshotProperties = typeof(TSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name);

        sourceProperties.Except(snapshotProperties).Should().BeEmpty();
    }

    private sealed class TestClock : IPipelineClock
    {
        public DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);
    }
}
