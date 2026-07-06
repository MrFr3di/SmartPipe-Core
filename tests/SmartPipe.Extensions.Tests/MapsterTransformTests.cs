using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FluentAssertions;
using Mapster;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Tests;

public class MapsterTransformTests
{
    [Fact]
    public async Task Transform_ShouldMapAllProperties()
    {
        var transform = new MapsterTransform<Source, Destination>();
        var ctx = ProcessingEnvelope<Source>.Create(new Source { Name = "Alice", Age = 25 });

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Alice");
        result.Value.Age.Should().Be(25);
    }

    [Fact]
    public async Task Transform_WithConfig_ShouldUseCustomMapping()
    {
        var config = new TypeAdapterConfig();
        config.NewConfig<RenamedSource, RenamedDestination>()
            .Map(destination => destination.DisplayName, source => source.Name);
        var transform = new MapsterTransform<RenamedSource, RenamedDestination>(config);
        var ctx = ProcessingEnvelope<RenamedSource>.Create(new RenamedSource { Name = "Alice" });

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value!.DisplayName.Should().Be("Alice");
    }

    [Fact]
    public async Task Transform_ToRecordWithInitOnlyProperties_ShouldMap()
    {
        var transform = new MapsterTransform<RecordSource, RecordDestination>();
        var ctx = ProcessingEnvelope<RecordSource>.Create(new RecordSource("order-1", 42));

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new RecordDestination { Id = "order-1", Count = 42 });
    }

    [Fact]
    public async Task Transform_WithNestedCollection_ShouldMapNestedValues()
    {
        var transform = new MapsterTransform<OrderSource, OrderDestination>();
        var ctx = ProcessingEnvelope<OrderSource>.Create(
            new OrderSource
            {
                Id = "order-1",
                Customer = new CustomerSource { Name = "Alice" },
                Lines =
                [
                    new OrderLineSource { Sku = "A", Quantity = 2 },
                    new OrderLineSource { Sku = "B", Quantity = 3 },
                ],
            }
        );

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be("order-1");
        result.Value.Customer!.Name.Should().Be("Alice");
        result.Value.Lines.Should()
            .BeEquivalentTo(
                [
                    new OrderLineDestination { Sku = "A", Quantity = 2 },
                    new OrderLineDestination { Sku = "B", Quantity = 3 },
                ]
            );
    }

    [Fact]
    public async Task Transform_WithStrictConfigFailure_ShouldReturnPermanentMappingFailure()
    {
        var config = new TypeAdapterConfig { RequireDestinationMemberSource = true };
        config.NewConfig<StrictSource, StrictDestination>();
        var transform = new MapsterTransform<StrictSource, StrictDestination>(config);
        var ctx = ProcessingEnvelope<StrictSource>.Create(new StrictSource { Name = "Alice" });

        var result = await transform.TransformAsync(ctx);

        result.IsSuccess.Should().BeFalse();
        result.Kind.Should().Be(StageResultKind.Failure);
        result.Error.Should().NotBeNull();
        result.Error!.Value.Type.Should().Be(ErrorType.Permanent);
        result.Error.Value.Category.Should().Be("Mapping");
        result.Error.Value.Message.Should().StartWith("Mapster mapping error:");
        result.Error.Value.InnerException.Should().NotBeNull();
        result.Error.Value.InnerException.Should().BeAssignableTo<CompileException>();
    }

    [Fact]
    public void MapsterTransform_ShouldDeclareAotAndTrimContract()
    {
        var type = typeof(MapsterTransform<,>);

        var trimAttribute = type.GetCustomAttribute<RequiresUnreferencedCodeAttribute>();
        var aotAttribute = type.GetCustomAttribute<RequiresDynamicCodeAttribute>();

        trimAttribute.Should().NotBeNull();
        trimAttribute!.Message.Should().Contain("not trimming-safe");
        trimAttribute.Message.Should().Contain("PipelineTransformer.FromFunc");
        aotAttribute.Should().NotBeNull();
        aotAttribute!.Message.Should().Contain("not NativeAOT-safe");
        aotAttribute.Message.Should().Contain("PipelineTransformer.FromFunc");
    }

    private sealed class Source
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    private sealed class Destination
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    private sealed class RenamedSource
    {
        public string Name { get; set; } = "";
    }

    private sealed class RenamedDestination
    {
        public string DisplayName { get; set; } = "";
    }

    private sealed record RecordSource(string Id, int Count);

    private sealed record RecordDestination
    {
        public string Id { get; init; } = "";
        public int Count { get; init; }
    }

    private sealed class OrderSource
    {
        public string Id { get; set; } = "";
        public CustomerSource? Customer { get; set; }
        public List<OrderLineSource> Lines { get; set; } = [];
    }

    private sealed class CustomerSource
    {
        public string Name { get; set; } = "";
    }

    private sealed class OrderLineSource
    {
        public string Sku { get; set; } = "";
        public int Quantity { get; set; }
    }

    private sealed class OrderDestination
    {
        public string Id { get; set; } = "";
        public CustomerDestination? Customer { get; set; }
        public List<OrderLineDestination> Lines { get; set; } = [];
    }

    private sealed class CustomerDestination
    {
        public string Name { get; set; } = "";
    }

    private sealed class OrderLineDestination
    {
        public string Sku { get; set; } = "";
        public int Quantity { get; set; }
    }

    private sealed class StrictSource
    {
        public string Name { get; set; } = "";
    }

    private sealed class StrictDestination
    {
        public string Name { get; set; } = "";
        public string RequiredValue { get; set; } = "";
    }
}
