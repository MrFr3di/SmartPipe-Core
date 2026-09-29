#nullable enable

using FluentAssertions;
using Mapster;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Mapster.Tests;

public sealed class MapsterMappingTests
{
    private static int _mapperInvocations;

    [Fact]
    public async Task Run_AppliesConfiguredAndNestedMapping()
    {
        var component = MapsterPipelineComponents.Transform<OrderSource, OrderDestination>(config =>
        {
            config.NewConfig<OrderSource, OrderDestination>();
            config.NewConfig<CustomerSource, CustomerDestination>()
                .Map(destination => destination.Name, source => source.Name + "!");
        });

        var results = await MapsterHarness.RunAsync(
            component,
            [new OrderSource { Id = "o1", Customer = new CustomerSource { Name = "Ada" } }]);

        results.Should().ContainSingle();
        results[0].Id.Should().Be("o1");
        results[0].Customer.Should().NotBeNull();
        results[0].Customer!.Name.Should().Be("Ada!");
    }

    [Fact]
    public async Task Run_IgnoresConfigurationAddedAfterComposition()
    {
        TypeAdapterConfig? workingConfiguration = null;
        var component = MapsterPipelineComponents.Transform<OrderSource, OrderDestination>(config =>
        {
            workingConfiguration = config;
            config.NewConfig<OrderSource, OrderDestination>();
            config.NewConfig<CustomerSource, CustomerDestination>()
                .Map(destination => destination.Name, source => source.Name + "!");
        });

        var before = await MapsterHarness.RunAsync(
            component,
            [new OrderSource { Id = "o1", Customer = new CustomerSource { Name = "Ada" } }]);

        workingConfiguration!.NewConfig<CustomerSource, CustomerDestination>()
            .Map(destination => destination.Name, source => source.Name + "?");

        var after = await MapsterHarness.RunAsync(
            component,
            [new OrderSource { Id = "o1", Customer = new CustomerSource { Name = "Ada" } }]);

        before[0].Customer!.Name.Should().Be("Ada!");
        after[0].Customer!.Name.Should().Be("Ada!");
    }

    [Fact]
    public async Task Run_PropagatesMappingExceptionsUnchanged()
    {
        var component = MapsterPipelineComponents.Transform<Source, Destination>(config =>
            config.NewConfig<Source, Destination>()
                .Map(destination => destination.Value, source => ThrowDuringMapping(source)));

        var outputs = await MapsterHarness.RunCollectingAsync(component, [new Source { N = 1 }]);

        outputs.Should().ContainSingle();
        var result = outputs[0].Result;
        result.IsFailure.Should().BeTrue();
        var error = result.Error!.Value;
        error.Category.Should().Be("StageException");
        error.Message.Should().Be("mapster-mapping-failure");
        error.Message.Should().NotStartWith("Mapster mapping error:");
        error.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("mapster-mapping-failure");
    }

    [Fact]
    public async Task Run_ChecksCancellationBeforeInvokingTheMapper()
    {
        Interlocked.Exchange(ref _mapperInvocations, 0);
        using var source = new CancellationTokenSource();
        var component = MapsterPipelineComponents.Transform<Source, Destination>(config =>
            config.NewConfig<Source, Destination>()
                .Map(destination => destination.Value, input => CountThenMap(input)));

        var exception = await MapsterHarness.RunExpectingFailureAsync(
            component,
            [new Source { N = 1 }],
            source.Token,
            source.Cancel);

        exception.Should().BeAssignableTo<OperationCanceledException>();
        Volatile.Read(ref _mapperInvocations).Should().Be(0);
    }

    [Fact]
    public async Task Run_ChecksCancellationAfterTheMapperReturns()
    {
        Interlocked.Exchange(ref _mapperInvocations, 0);
        using var source = new CancellationTokenSource();
        var component = MapsterPipelineComponents.Transform<Source, Destination>(config =>
            config.NewConfig<Source, Destination>()
                .Map(destination => destination.Value, input => CountThenCancel(input, source)));

        var exception = await MapsterHarness.RunExpectingFailureAsync(component, [new Source { N = 1 }], source.Token);

        exception.Should().BeAssignableTo<OperationCanceledException>();
        Volatile.Read(ref _mapperInvocations).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentRuns_ShareTheCapturedDelegateWithoutCrossTalk()
    {
        var component = MapsterPipelineComponents.Transform<Source, Destination>(config =>
            config.NewConfig<Source, Destination>().Map(destination => destination.Value, source => source.N * 2));

        var runs = await Task.WhenAll(
            MapsterHarness.RunAsync(component, [new Source { N = 1 }, new Source { N = 2 }]),
            MapsterHarness.RunAsync(component, [new Source { N = 3 }, new Source { N = 4 }]),
            MapsterHarness.RunAsync(component, [new Source { N = 5 }]));

        runs[0].Select(item => item.Value).Should().Equal(2, 4);
        runs[1].Select(item => item.Value).Should().Equal(6, 8);
        runs[2].Select(item => item.Value).Should().Equal(10);
    }

    private static int ThrowDuringMapping(Source source)
    {
        _ = source;
        throw new InvalidOperationException("mapster-mapping-failure");
    }

    private static int CountThenMap(Source source)
    {
        Interlocked.Increment(ref _mapperInvocations);
        return source.N;
    }

    private static int CountThenCancel(Source source, CancellationTokenSource cancellation)
    {
        Interlocked.Increment(ref _mapperInvocations);
        cancellation.Cancel();
        return source.N;
    }
}
