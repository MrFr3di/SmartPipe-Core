using Microsoft.Extensions.Logging;
using SmartPipe.Core;
using SmartPipe.Extensions.Hosting.Tests.Fakes;

namespace SmartPipe.Extensions.Hosting.Tests.Runtime;

[Trait("Category", "HostingLifecycle")]
public sealed class SmartPipeHostedOrchestratorMonitoringTests
{
    [Fact]
    public async Task NormalCompletion_DefaultKeepsHostAlive()
    {
        var completion = NewCompletion();
        var run = new ControlledHostedRun("orders") { Completion = completion.Task };
        using var lifetime = new RecordingHostApplicationLifetime();
        var logger = new RecordingLogger<SmartPipeHostedOrchestrator>();
        using var orchestrator = CreateOrchestrator(
            lifetime,
            logger,
            [CreateRegistration(run, completionBehavior: SmartPipeHostedCompletionBehavior.KeepHostAlive)]);
        await orchestrator.StartAsync(TestContext.Current.CancellationToken);

        completion.SetResult();
        await orchestrator.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, lifetime.StopApplicationCalls);
    }

    [Fact]
    public async Task NormalCompletion_StopApplicationRequestsShutdownOnce()
    {
        var completion = NewCompletion();
        var run = new ControlledHostedRun("orders") { Completion = completion.Task };
        using var lifetime = new RecordingHostApplicationLifetime();
        var logger = new RecordingLogger<SmartPipeHostedOrchestrator>();
        using var orchestrator = CreateOrchestrator(
            lifetime,
            logger,
            [CreateRegistration(run, completionBehavior: SmartPipeHostedCompletionBehavior.StopApplication)]);
        await orchestrator.StartAsync(TestContext.Current.CancellationToken);

        completion.SetResult();
        await orchestrator.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, lifetime.StopApplicationCalls);
    }

    [Fact]
    public async Task Fault_StopApplicationRequestsShutdownWithoutFaultingMonitor()
    {
        var error = new InvalidOperationException("fault");
        var completion = NewCompletion();
        var run = new ControlledHostedRun("orders") { Completion = completion.Task };
        using var lifetime = new RecordingHostApplicationLifetime();
        var logger = new RecordingLogger<SmartPipeHostedOrchestrator>();
        using var orchestrator = CreateOrchestrator(
            lifetime,
            logger,
            [CreateRegistration(run, failureBehavior: SmartPipeHostedPipelineFailureBehavior.StopApplication)]);
        await orchestrator.StartAsync(TestContext.Current.CancellationToken);

        completion.SetException(error);
        await orchestrator.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, lifetime.StopApplicationCalls);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Fault_RethrowPropagatesImmediatelyWithoutWaitingForOtherRun()
    {
        var error = new InvalidOperationException("fault");
        var firstCompletion = NewCompletion();
        var secondCompletion = NewCompletion();
        var first = new ControlledHostedRun("first") { Completion = firstCompletion.Task };
        var second = new ControlledHostedRun("second") { Completion = secondCompletion.Task };
        using var lifetime = new RecordingHostApplicationLifetime();
        var logger = new RecordingLogger<SmartPipeHostedOrchestrator>();
        using var orchestrator = CreateOrchestrator(
            lifetime,
            logger,
            [
                CreateRegistration(first, failureBehavior: SmartPipeHostedPipelineFailureBehavior.Rethrow),
                CreateRegistration(second, registrationOrder: 1),
            ]);
        await orchestrator.StartAsync(TestContext.Current.CancellationToken);

        firstCompletion.SetException(error);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Same(error, thrown);
        Assert.False(secondCompletion.Task.IsCompleted);
    }

    [Theory]
    [InlineData(SmartPipeHostedPipelineFailureBehavior.MarkUnhealthyAndKeepHostAlive)]
    [InlineData(SmartPipeHostedPipelineFailureBehavior.Ignore)]
    public async Task KeepAliveFaultBehaviors_ContinueMonitoringRemainingRuns(
        SmartPipeHostedPipelineFailureBehavior behavior)
    {
        var firstCompletion = NewCompletion();
        var secondCompletion = NewCompletion();
        var first = new ControlledHostedRun("first") { Completion = firstCompletion.Task };
        var second = new ControlledHostedRun("second") { Completion = secondCompletion.Task };
        using var lifetime = new RecordingHostApplicationLifetime();
        var logger = new RecordingLogger<SmartPipeHostedOrchestrator>();
        using var orchestrator = CreateOrchestrator(
            lifetime,
            logger,
            [
                CreateRegistration(first, failureBehavior: behavior),
                CreateRegistration(second, registrationOrder: 1),
            ]);
        await orchestrator.StartAsync(TestContext.Current.CancellationToken);

        firstCompletion.SetException(new InvalidOperationException("fault"));
        secondCompletion.SetResult();
        await orchestrator.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, lifetime.StopApplicationCalls);
        Assert.Contains(logger.Entries, entry =>
            entry.Properties.TryGetValue("PipelineKey", out var key)
            && Equals(key, "first")
            && entry.Properties.ContainsKey("RunId")
            && entry.Properties.ContainsKey("Order")
            && entry.Properties.ContainsKey("FailureBehavior")
            && entry.Properties.ContainsKey("DrainTimeout")
            && entry.Properties.ContainsKey("PipelineState")
            && Equals(entry.Properties["Operation"], "Monitor"));
    }

    [Fact]
    public async Task SimultaneousRethrowFaults_PropagateFirstStartupOrderError()
    {
        var firstError = new InvalidOperationException("first");
        var secondError = new InvalidOperationException("second");
        var firstCompletion = NewCompletion();
        var secondCompletion = NewCompletion();
        firstCompletion.SetException(firstError);
        secondCompletion.SetException(secondError);
        var first = new ControlledHostedRun("first") { Completion = firstCompletion.Task };
        var second = new ControlledHostedRun("second") { Completion = secondCompletion.Task };
        using var lifetime = new RecordingHostApplicationLifetime();
        var logger = new RecordingLogger<SmartPipeHostedOrchestrator>();
        using var orchestrator = CreateOrchestrator(
            lifetime,
            logger,
            [
                CreateRegistration(first, failureBehavior: SmartPipeHostedPipelineFailureBehavior.Rethrow),
                CreateRegistration(
                    second,
                    registrationOrder: 1,
                    failureBehavior: SmartPipeHostedPipelineFailureBehavior.Rethrow),
            ]);

        await orchestrator.StartAsync(TestContext.Current.CancellationToken);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Same(firstError, thrown);
        Assert.True(secondCompletion.Task.Exception is not null);
    }

    private static SmartPipeHostedOrchestrator CreateOrchestrator(
        RecordingHostApplicationLifetime lifetime,
        RecordingLogger<SmartPipeHostedOrchestrator> logger,
        IEnumerable<IHostedPipelineRegistration> registrations) =>
        new(registrations, lifetime, logger);

    private static ControlledHostedRegistration CreateRegistration(
        ControlledHostedRun run,
        int registrationOrder = 0,
        SmartPipeHostedPipelineFailureBehavior failureBehavior =
            SmartPipeHostedPipelineFailureBehavior.StopApplication,
        SmartPipeHostedCompletionBehavior completionBehavior =
            SmartPipeHostedCompletionBehavior.KeepHostAlive) =>
        new(
            new HostedPipelineDescriptor
            {
                Key = run.Key,
                InputType = typeof(int),
                OutputType = typeof(int),
                Order = 0,
                RegistrationOrder = registrationOrder,
                DrainTimeout = TimeSpan.FromSeconds(30),
                FailureBehavior = failureBehavior,
                CompletionBehavior = completionBehavior,
            },
            _ => Task.FromResult<IHostedPipelineRun>(run));

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
