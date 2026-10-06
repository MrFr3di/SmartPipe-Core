using System.Reflection;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineComponentTests
{
    [Fact]
    public void Ownership_Values_MatchContract()
    {
        ((int)PipelineComponentOwnership.RuntimeOwned).Should().Be(0);
        ((int)PipelineComponentOwnership.ScopeOwned).Should().Be(1);
        ((int)PipelineComponentOwnership.ExternallyOwned).Should().Be(2);
    }

    [Fact]
    public void RuntimeOwned_SetsOwnershipInitializationAndPerRun()
    {
        var descriptor = PipelineComponent.RuntimeOwned<TestComponent>(
            (_, _) => ValueTask.FromResult(new TestComponent()));

        descriptor.Ownership.Should().Be(PipelineComponentOwnership.RuntimeOwned);
        descriptor.Initialize.Should().BeTrue();
        descriptor.IsPerRun.Should().BeTrue();
    }

    [Fact]
    public void ScopeOwned_SetsOwnershipInitializationAndPerRun()
    {
        var descriptor = PipelineComponent.ScopeOwned<TestComponent>(
            (_, _) => ValueTask.FromResult(new TestComponent()));

        descriptor.Ownership.Should().Be(PipelineComponentOwnership.ScopeOwned);
        descriptor.Initialize.Should().BeTrue();
        descriptor.IsPerRun.Should().BeTrue();
    }

    [Fact]
    public async Task Borrowed_Default_CapturesInstanceWithoutInitialization()
    {
        var instance = new TestComponent();
        var descriptor = PipelineComponent.Borrowed(instance);

        descriptor.Ownership.Should().Be(PipelineComponentOwnership.ExternallyOwned);
        descriptor.Initialize.Should().BeFalse();
        descriptor.IsPerRun.Should().BeFalse();

        (await descriptor.Activator(CreateContext(), CancellationToken.None))
            .Should().BeSameAs(instance);
    }

    [Fact]
    public void Borrowed_InitializeTrue_EnablesInitialization()
    {
        var descriptor = PipelineComponent.Borrowed(new TestComponent(), initialize: true);

        descriptor.Initialize.Should().BeTrue();
        descriptor.IsPerRun.Should().BeFalse();
    }

    [Fact]
    public void RuntimeOwned_NullFactory_Throws()
    {
        var act = () => PipelineComponent.RuntimeOwned<TestComponent>(null!);

        act.Should().ThrowExactly<ArgumentNullException>();
    }

    [Fact]
    public void ScopeOwned_NullFactory_Throws()
    {
        var act = () => PipelineComponent.ScopeOwned<TestComponent>(null!);

        act.Should().ThrowExactly<ArgumentNullException>();
    }

    [Fact]
    public void Borrowed_NullInstance_Throws()
    {
        var act = () => PipelineComponent.Borrowed<TestComponent>(null!);

        act.Should().ThrowExactly<ArgumentNullException>();
    }

    [Fact]
    public async Task FactoryRoutes_AreLazyUntilActivatorCalled()
    {
        var runtimeCalls = 0;
        var scopeCalls = 0;
        var runtime = PipelineComponent.RuntimeOwned<TestComponent>((_, _) =>
        {
            runtimeCalls++;
            return ValueTask.FromResult(new TestComponent());
        });
        var scope = PipelineComponent.ScopeOwned<TestComponent>((_, _) =>
        {
            scopeCalls++;
            return ValueTask.FromResult(new TestComponent());
        });

        runtimeCalls.Should().Be(0);
        scopeCalls.Should().Be(0);

        await runtime.Activator(CreateContext(), CancellationToken.None);
        await scope.Activator(CreateContext(), CancellationToken.None);

        runtimeCalls.Should().Be(1);
        scopeCalls.Should().Be(1);
    }

    [Fact]
    public async Task RuntimeOwned_FactoryReceivesExactContextAndToken()
    {
        PipelineActivationContext? observedContext = null;
        CancellationToken observedToken = default;
        var descriptor = PipelineComponent.RuntimeOwned<TestComponent>((context, token) =>
        {
            observedContext = context;
            observedToken = token;
            return ValueTask.FromResult(new TestComponent());
        });
        var expectedContext = CreateContext();
        using var cancellation = new CancellationTokenSource();

        await descriptor.Activator(expectedContext, cancellation.Token);

        observedContext.Should().BeSameAs(expectedContext);
        observedToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task ScopeOwned_FactoryReceivesExactContextAndToken()
    {
        PipelineActivationContext? observedContext = null;
        CancellationToken observedToken = default;
        var descriptor = PipelineComponent.ScopeOwned<TestComponent>((context, token) =>
        {
            observedContext = context;
            observedToken = token;
            return ValueTask.FromResult(new TestComponent());
        });
        var expectedContext = CreateContext();
        using var cancellation = new CancellationTokenSource();

        await descriptor.Activator(expectedContext, cancellation.Token);

        observedContext.Should().BeSameAs(expectedContext);
        observedToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task Activator_NullFactoryResult_RemainsNull()
    {
        var descriptor = PipelineComponent.RuntimeOwned<TestComponent>(
            (_, _) => ValueTask.FromResult<TestComponent>(null!));

        var result = await descriptor.Activator(CreateContext(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public void PublicSurface_HasNoConstructorAndExactlyThreeFactoryRoutes()
    {
        typeof(PipelineComponent<TestComponent>)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().BeEmpty();
        typeof(PipelineComponent<TestComponent>).IsSealed.Should().BeTrue();

        typeof(PipelineComponent)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.IsGenericMethodDefinition)
            .Select(method => method.Name)
            .Should().BeEquivalentTo("RuntimeOwned", "ScopeOwned", "Borrowed");
    }

    private static PipelineActivationContext CreateContext() =>
        new(new PipelineKey("orders"), Guid.NewGuid());

    private sealed class TestComponent
    {
    }
}
