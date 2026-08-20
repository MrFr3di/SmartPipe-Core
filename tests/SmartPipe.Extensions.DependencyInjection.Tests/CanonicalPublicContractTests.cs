using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.DependencyInjection.Tests;

public sealed class CanonicalPublicContractTests
{
    [Fact]
    public void Builders_ExposeOnlyCollectionAndTypedDefinitionIdentity()
    {
        Assert.Equal(
            [nameof(ISmartPipeBuilder.Services)],
            typeof(ISmartPipeBuilder).GetProperties().Select(property => property.Name));
        Assert.Equal(typeof(IServiceCollection), typeof(ISmartPipeBuilder).GetProperty(nameof(ISmartPipeBuilder.Services))!.PropertyType);

        var properties = typeof(ISmartPipeRegistrationBuilder<int, string>).GetProperties();
        Assert.Equal([nameof(ISmartPipeRegistrationBuilder<int, string>.Key), nameof(ISmartPipeRegistrationBuilder<int, string>.Definition)], properties.Select(property => property.Name));
        Assert.Equal(typeof(PipelineKey), properties[0].PropertyType);
        Assert.Equal(typeof(PipelineDefinition<int, string>), properties[1].PropertyType);
    }

    [Fact]
    public void RunFactory_IsAsyncOnlyAndProviderTryGetIsNullabilityAnnotated()
    {
        var factory = typeof(ISmartPipeRunFactory<int, string>);
        var start = Assert.Single(factory.GetMethods());

        Assert.Equal(nameof(ISmartPipeRunFactory<int, string>.StartAsync), start.Name);
        Assert.Equal(typeof(Task<PipelineRun<string>>), start.ReturnType);
        Assert.Null(factory.GetMethod("Start"));

        var tryGet = typeof(ISmartPipeFactoryProvider).GetMethod(nameof(ISmartPipeFactoryProvider.TryGetFactory))!;
        var outParameter = tryGet.GetParameters()[1];
        Assert.True(outParameter.IsOut);
        Assert.NotNull(outParameter.GetCustomAttribute<NotNullWhenAttribute>());
    }

    [Fact]
    public void RegistryContracts_ExposeDefensiveSnapshotAndExactLookupShapes()
    {
        var registry = typeof(ISmartPipeRegistry);

        Assert.Equal(typeof(IReadOnlyList<SmartPipeRegistrationDescriptor>), registry.GetMethod(nameof(ISmartPipeRegistry.GetRegistrations))!.ReturnType);
        Assert.Equal(typeof(SmartPipeRegistrationDescriptor), registry.GetMethod(nameof(ISmartPipeRegistry.GetRegistration))!.ReturnType);
        var tryGet = registry.GetMethod(nameof(ISmartPipeRegistry.TryGetRegistration))!;
        Assert.Equal(typeof(bool), tryGet.ReturnType);
        Assert.NotNull(tryGet.GetParameters()[1].GetCustomAttribute<NotNullWhenAttribute>());

        var activeRuns = typeof(ISmartPipeRunRegistry).GetMethod(nameof(ISmartPipeRunRegistry.GetActiveRuns))!;
        Assert.Equal(typeof(IReadOnlyList<SmartPipeRunSnapshot>), activeRuns.ReturnType);
    }

    [Fact]
    public void ImmutableMetadata_RoundTripsFrozenValues()
    {
        var key = new PipelineKey("orders");
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.Parse("2035-04-05T06:07:08Z");
        var identity = new SmartPipeRunIdentity { PipelineKey = key, RunId = runId };
        var descriptor = new SmartPipeRegistrationDescriptor
        {
            Key = key,
            InputType = typeof(int),
            OutputType = typeof(string),
            DefinitionType = typeof(PipelineDefinition<int, string>),
            FactoryType = typeof(ISmartPipeRunFactory<int, string>),
            DisplayName = "orders",
            RegistrationOrder = 0,
            IsReusable = false,
        };
        var snapshot = new SmartPipeRunSnapshot
        {
            Identity = identity,
            InputType = typeof(int),
            OutputType = typeof(string),
            StartedAtUtc = startedAt,
            State = PipelineRunState.Running,
            Metrics = SmartPipeMetricsSnapshot.Empty,
            InputCapacity = 17,
            OutputCapacity = 23,
        };

        Assert.Equal(key, descriptor.Key);
        Assert.Equal(typeof(PipelineDefinition<int, string>), descriptor.DefinitionType);
        Assert.Equal(typeof(ISmartPipeRunFactory<int, string>), descriptor.FactoryType);
        Assert.Equal(0, descriptor.RegistrationOrder);
        Assert.False(descriptor.IsReusable);
        Assert.Equal(runId, snapshot.Identity.RunId);
        Assert.Equal(startedAt, snapshot.StartedAtUtc);
        Assert.Equal(17, snapshot.InputCapacity);
        Assert.Equal(23, snapshot.OutputCapacity);
    }

    [Fact]
    public void CanonicalLeaf_HasNoFacadeReferenceOrLegacyFactoryTypes()
    {
        var assembly = typeof(ISmartPipeBuilder).Assembly;

        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => reference.Name == "SmartPipe.Extensions");
        Assert.DoesNotContain(
            assembly.GetExportedTypes(),
            type => type.FullName is not null
                && (type.FullName.StartsWith("SmartPipe.Extensions.ISmartPipeFactory", StringComparison.Ordinal)
                    || type.FullName.StartsWith("SmartPipe.Extensions.SmartPipeFactory", StringComparison.Ordinal)));
    }
}
