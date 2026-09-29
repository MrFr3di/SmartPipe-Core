#nullable enable

using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SmartPipe.Core;

namespace SmartPipe.Extensions.EntityFrameworkCore.Tests;

public sealed class EfCoreContractTests
{
    [Fact]
    public void Leaf_ReferencesOnlyCoreEntityFrameworkCoreAndLoggingAbstractions()
    {
        var referenced = typeof(EfCorePipelineComponents).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();

        referenced.Should().Contain("SmartPipe.Core");
        referenced.Should().Contain("Microsoft.EntityFrameworkCore");
        referenced.Should().NotContain(name => name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase));
        referenced.Should().NotContain(name => name.Contains("InMemory", StringComparison.OrdinalIgnoreCase));
        referenced.Should().NotContain(name => name.Contains("SqlServer", StringComparison.OrdinalIgnoreCase));
        referenced.Should().NotContain(name => name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
        referenced.Should().NotContain("SmartPipe.Extensions");
    }

    [Fact]
    public void Leaf_ExposesNoWriteSinkOrUnitOfWorkSurface()
    {
        var exported = typeof(EfCorePipelineComponents).Assembly.GetExportedTypes().ToArray();

        exported.Should().NotContain(type => type.Name.Contains("Sink", StringComparison.OrdinalIgnoreCase));
        exported.Should().NotContain(type => type.Name.Contains("Repository", StringComparison.OrdinalIgnoreCase));
        exported.Should().NotContain(type => type.Name.Contains("UnitOfWork", StringComparison.OrdinalIgnoreCase));
        exported.Should().NotContain(type => type.Name.Contains("Transaction", StringComparison.OrdinalIgnoreCase));
        exported
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Should()
            .NotContain(member => member.Name.Contains("SaveChanges", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Components_ExposeBothAcquisitionFormsForEachPath()
    {
        var methods = typeof(EfCorePipelineComponents)
            .GetMethods(BindingFlags.Public | BindingFlags.Static);

        methods.Count(method => method.Name == "QuerySource").Should().Be(2);
        methods.Count(method => method.Name == "CompiledQuerySource").Should().Be(2);
        methods.Should().OnlyContain(method => method.IsGenericMethodDefinition);
    }

    [Fact]
    public void QueryablePath_ConstrainsTheResultWhileTheCompiledPathDoesNot()
    {
        var queryableArguments = typeof(EfCorePipelineComponents)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "QuerySource")
            .Select(method => method.GetGenericArguments()[1]);
        var compiledArguments = typeof(EfCorePipelineComponents)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "CompiledQuerySource")
            .Select(method => method.GetGenericArguments()[1]);

        queryableArguments.Should().OnlyContain(argument =>
            argument.GenericParameterAttributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint));
        compiledArguments.Should().OnlyContain(argument =>
            !argument.GenericParameterAttributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint));
    }

    [Fact]
    public void CompiledOptions_ExposeNoTrackingOption()
    {
        typeof(EfCoreQueryOptions).GetProperty("TrackingMode").Should().NotBeNull();
        typeof(EfCoreCompiledQueryOptions).GetProperty("TrackingMode").Should().BeNull();
        typeof(EfCoreQueryOptions).GetProperty("OperationName").Should().NotBeNull();
        typeof(EfCoreCompiledQueryOptions).GetProperty("OperationName").Should().NotBeNull();
    }

    [Fact]
    public void Options_ExposeTheDocumentedDefaultsAndEnumValues()
    {
        new EfCoreQueryOptions().OperationName.Should().Be("query");
        new EfCoreQueryOptions().TrackingMode.Should().Be(EfCoreQueryTrackingMode.NoTracking);
        new EfCoreCompiledQueryOptions().OperationName.Should().Be("compiled-query");

        Enum.GetValues<EfCoreQueryTrackingMode>().Should().Equal(
            EfCoreQueryTrackingMode.NoTracking,
            EfCoreQueryTrackingMode.NoTrackingWithIdentityResolution,
            EfCoreQueryTrackingMode.Tracking,
            EfCoreQueryTrackingMode.PreserveQuery);
    }

    [Fact]
    public void DefinitionBuilder_ReturnsOrdinaryCoreTypedBuilders()
    {
        var methods = typeof(EfCorePipelineDefinitionBuilder)
            .GetMethods(BindingFlags.Public | BindingFlags.Static);

        methods.Count(method => method.Name == "FromQuery").Should().Be(2);
        methods.Count(method => method.Name == "FromCompiledQuery").Should().Be(2);
        methods.Should().OnlyContain(method =>
            method.ReturnType.IsGenericType
            && method.ReturnType.GetGenericTypeDefinition() == typeof(PipelineDefinitionBuilder<>));
    }

    [Fact]
    public void Leaf_ComposesIntoARealCoreDefinitionWithoutTouchingTheProvider()
    {
        var factory = new RecordingContextFactory();
        var queryFactoryCalls = 0;

        var definition = EfCorePipelineDefinitionBuilder
            .FromQuery<TestDbContext, TestRow>(
                new PipelineKey("sp220-11-definition"),
                factory.CreateAsync,
                (context, activation) =>
                {
                    queryFactoryCalls++;
                    return new RecordingQueryable<TestRow>([new TestRow { Id = 1, Name = "Ada" }], "definition");
                },
                new EfCoreQueryOptions { OperationName = "sp220-11-definition" })
            .Build();

        definition.Should().NotBeNull();
        factory.CallCount.Should().Be(0);
        queryFactoryCalls.Should().Be(0);
    }
}
