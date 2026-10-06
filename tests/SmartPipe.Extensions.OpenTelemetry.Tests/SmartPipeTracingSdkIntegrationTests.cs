#nullable enable

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using SmartPipe.Core;
using SmartPipe.Extensions.OpenTelemetry;

namespace SmartPipe.Extensions.OpenTelemetry.Tests;

[Collection("otel-sdk-integration")]
public class SmartPipeTracingSdkIntegrationTests
{
    private static readonly HashSet<string> FrozenTagKeys =
    [
        "smartpipe.pipeline_id",
        "smartpipe.run_id",
        "smartpipe.trace_id",
        "smartpipe.stage_id",
        "smartpipe.stage_name",
        "smartpipe.parallelism",
        "smartpipe.input_capacity",
        "smartpipe.output_capacity",
    ];

    [Fact]
    public async Task RegisteredSource_ExportsPipelineActivitiesWithPreservedOperationNames()
    {
        var exported = new List<Activity>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry()
            .WithTracing(builder => builder.AddInMemoryExporter(exported))
            .AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var tracerProvider = provider.GetRequiredService<TracerProvider>();

        await using (var run = SdkPipelineFixtures.StartPipeline(1, TestContext.Current.CancellationToken))
            await run.Completion;

        tracerProvider.ForceFlush();

        exported.Should().NotBeEmpty();
        exported.Select(activity => activity.OperationName).Should().Contain("Pipeline.Run");
        exported.Select(activity => activity.OperationName).Should().Contain("Transform");
        exported.Should().OnlyContain(
            activity => activity.Source.Name == SmartPipeDiagnostics.ActivitySourceName,
            "no adapter wrapper span may be introduced");
    }

    [Fact]
    public async Task ExportedActivities_ContainOnlyTheFrozenOperationNames()
    {
        var exported = new List<Activity>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry()
            .WithTracing(builder => builder.AddInMemoryExporter(exported))
            .AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var tracerProvider = provider.GetRequiredService<TracerProvider>();

        await using (var run = SdkPipelineFixtures.StartPipeline(2, TestContext.Current.CancellationToken))
            await run.Completion;

        tracerProvider.ForceFlush();

        exported.Select(activity => activity.OperationName).Should()
            .OnlyContain(name => name == "Pipeline.Run" || name == "Transform");
    }

    [Fact]
    public async Task ParentPropagation_IsPreservedThroughSdkExport()
    {
        var exported = new List<Activity>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry()
            .WithTracing(builder => builder.AddInMemoryExporter(exported))
            .AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var tracerProvider = provider.GetRequiredService<TracerProvider>();

        using var parentSource = new ActivitySource("SmartPipe.Tests.Parent");
        var parentStopped = new List<Activity>();
        using var parentListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SmartPipe.Tests.Parent",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => parentStopped.Add(activity),
        };
        ActivitySource.AddActivityListener(parentListener);

        using (var parent = parentSource.StartActivity("Parent.Operation")!)
        {
            await using var run = SdkPipelineFixtures.StartPipeline(1, TestContext.Current.CancellationToken);
            await run.Completion;
        }

        tracerProvider.ForceFlush();

        var parentSpanId = parentStopped.Single().SpanId;
        var pipelineRunActivity = exported.Single(activity => activity.OperationName == "Pipeline.Run");
        pipelineRunActivity.ParentSpanId.Should().Be(parentSpanId);
    }

    [Fact]
    public async Task StatusSemantics_ArePreservedThroughSdkExport()
    {
        var exported = new List<Activity>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry()
            .WithTracing(builder => builder.AddInMemoryExporter(exported))
            .AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var tracerProvider = provider.GetRequiredService<TracerProvider>();

        await using (var success = SdkPipelineFixtures.StartPipeline(1, TestContext.Current.CancellationToken))
            await success.Completion;
        await using (var failure = SdkPipelineFixtures.StartFailingPipeline(TestContext.Current.CancellationToken))
            await failure.Completion;

        tracerProvider.ForceFlush();

        exported.Where(activity => activity.OperationName == "Pipeline.Run")
            .Should().OnlyContain(activity => activity.Status == ActivityStatusCode.Ok);
        exported.Where(activity => activity.OperationName == "Transform")
            .Should().Contain(activity => activity.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task RepeatedSuccessfulRegistration_DoesNotDuplicateSpans()
    {
        var exported = new List<Activity>();
        var services = new ServiceCollection();
        var builder = services.AddOpenTelemetry()
            .WithTracing(builder => builder.AddInMemoryExporter(exported));
        builder.AddSmartPipeInstrumentation();
        builder.AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var tracerProvider = provider.GetRequiredService<TracerProvider>();

        await using (var run = SdkPipelineFixtures.StartPipeline(1, TestContext.Current.CancellationToken))
            await run.Completion;

        tracerProvider.ForceFlush();

        exported.Count(activity => activity.OperationName == "Pipeline.Run").Should().Be(1);
        exported.Count(activity => activity.OperationName == "Transform").Should().Be(1);
    }

    [Fact]
    public async Task ExportedActivities_CarryOnlyFrozenTagKeys()
    {
        var exported = new List<Activity>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry()
            .WithTracing(builder => builder.AddInMemoryExporter(exported))
            .AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var tracerProvider = provider.GetRequiredService<TracerProvider>();

        await using (var run = SdkPipelineFixtures.StartPipeline(1, TestContext.Current.CancellationToken))
            await run.Completion;

        tracerProvider.ForceFlush();

        foreach (var activity in exported)
        {
            foreach (var tag in activity.TagObjects)
            {
                FrozenTagKeys.Should().Contain(tag.Key,
                    "activity tags must stay within the frozen low-cardinality contract");
            }
        }

        var tagKeys = exported.SelectMany(activity => activity.TagObjects)
            .Select(tag => tag.Key)
            .ToArray();
        tagKeys.Should().NotContain(key =>
            key.Contains("payload", StringComparison.OrdinalIgnoreCase)
            || key.Contains("error", StringComparison.OrdinalIgnoreCase)
            || key.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || key.Contains("identity", StringComparison.OrdinalIgnoreCase)
            || key.Contains("uri", StringComparison.OrdinalIgnoreCase)
            || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || key.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cancellation_PreservesCoreErrorStatusBehavior()
    {
        var exported = new List<Activity>();
        var services = new ServiceCollection();
        services.AddOpenTelemetry()
            .WithTracing(builder => builder.AddInMemoryExporter(exported))
            .AddSmartPipeInstrumentation();

        using var provider = services.BuildServiceProvider();
        using var tracerProvider = provider.GetRequiredService<TracerProvider>();

        using var cts = new CancellationTokenSource();
        await using var run = SdkPipelineFixtures.StartInfinitePipeline(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Completion);

        tracerProvider.ForceFlush();

        var pipelineRunActivity = exported.Single(activity => activity.OperationName == "Pipeline.Run");
        pipelineRunActivity.Status.Should().Be(ActivityStatusCode.Error);
    }
}
