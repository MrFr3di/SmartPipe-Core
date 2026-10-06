using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineActivationContextTests
{
    [Fact]
    public void Constructor_DefaultPipelineKey_Throws()
    {
        var act = () => new PipelineActivationContext(default, Guid.NewGuid());

        act.Should().ThrowExactly<ArgumentException>();
    }

    [Fact]
    public void Constructor_EmptyRunId_Throws()
    {
        var act = () => new PipelineActivationContext(new PipelineKey("orders"), Guid.Empty);

        act.Should().ThrowExactly<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullTimeProvider_UsesSystem()
    {
        var context = new PipelineActivationContext(new PipelineKey("orders"), Guid.NewGuid());

        context.TimeProvider.Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public void Constructor_ExplicitTimeProvider_PreservesInstance()
    {
        var provider = new FakeTimeProvider();

        var context = new PipelineActivationContext(
            new PipelineKey("orders"),
            Guid.NewGuid(),
            timeProvider: provider);

        context.TimeProvider.Should().BeSameAs(provider);
    }

    [Fact]
    public void Constructor_OmittedTimeProvider_IsNotExplicit()
    {
        var context = new PipelineActivationContext(new PipelineKey("orders"), Guid.NewGuid());

        context.HasExplicitTimeProvider.Should().BeFalse();
    }

    [Fact]
    public void Constructor_ExplicitSystemTimeProvider_IsExplicit()
    {
        var context = new PipelineActivationContext(
            new PipelineKey("orders"),
            Guid.NewGuid(),
            timeProvider: TimeProvider.System);

        context.HasExplicitTimeProvider.Should().BeTrue();
    }

    [Fact]
    public void Constructor_ServiceProvider_PreservesInstance()
    {
        var services = new TestServiceProvider();

        var context = new PipelineActivationContext(
            new PipelineKey("orders"),
            Guid.NewGuid(),
            services: services);

        context.Services.Should().BeSameAs(services);
    }

    [Fact]
    public void Properties_HaveNoSetters()
    {
        var type = typeof(PipelineActivationContext);

        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().NotContain(constructor => constructor.GetParameters().Length == 0);
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Should().NotContain(property => property.CanWrite);
    }

    private sealed class TestServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
