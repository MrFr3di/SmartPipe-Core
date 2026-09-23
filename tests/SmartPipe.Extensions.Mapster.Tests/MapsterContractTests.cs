#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FluentAssertions;
using Mapster;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Mapster.Tests;

public sealed class MapsterContractTests
{
    [Fact]
    public void Transform_DeclaresTrimAndDynamicCodeAnnotations()
    {
        var method = typeof(MapsterPipelineComponents)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate => candidate.Name == nameof(MapsterPipelineComponents.Transform));

        var trim = method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>();
        var dynamicCode = method.GetCustomAttribute<RequiresDynamicCodeAttribute>();

        trim.Should().NotBeNull();
        trim!.Message.Should().Be("Mapster runtime mapping uses reflection metadata.");
        dynamicCode.Should().NotBeNull();
        dynamicCode!.Message.Should().Be("Mapster runtime mapping compiles expressions at runtime.");
    }

    [Fact]
    public void BuilderExtensions_DeclareTrimAndDynamicCodeAnnotations()
    {
        var methods = typeof(MapsterPipelineDefinitionBuilderExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(candidate => candidate.Name == nameof(MapsterPipelineDefinitionBuilderExtensions.MapWithMapster))
            .ToArray();

        methods.Should().HaveCount(2);
        foreach (var method in methods)
        {
            method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>().Should().NotBeNull();
            method.GetCustomAttribute<RequiresDynamicCodeAttribute>().Should().NotBeNull();
        }
    }

    [Fact]
    public void PublicSurface_ExposesNoCallerOwnedConfigurationParameter()
    {
        var parameters = typeof(MapsterPipelineComponents)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(method => method.GetParameters())
            .Concat(typeof(MapsterPipelineDefinitionBuilderExtensions)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SelectMany(method => method.GetParameters()))
            .ToArray();

        parameters.Should().NotContain(parameter => parameter.ParameterType == typeof(TypeAdapterConfig));
        parameters.Should().Contain(parameter => parameter.ParameterType == typeof(Action<TypeAdapterConfig>));
    }

    [Fact]
    public void Components_ReturnRuntimeOwnedDescriptors()
    {
        var component = MapsterPipelineComponents.Transform<Source, Destination>();

        component.Ownership.Should().Be(PipelineComponentOwnership.RuntimeOwned);
        component.Initialize.Should().BeTrue();
    }

    [Fact]
    public void LeafAssembly_ReferencesOnlyCoreAndMapster()
    {
        var referenced = typeof(MapsterPipelineComponents).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToArray();

        referenced.Should().Contain("SmartPipe.Core");
        referenced.Should().Contain("Mapster");
        referenced.Should().NotContain("SmartPipe.Extensions");
        referenced.Should().NotContain("SmartPipe.Extensions.DependencyInjection");
        referenced.Should().NotContain("SmartPipe.Extensions.Hosting");
        referenced.Should().NotContain("SmartPipe.Extensions.Json");
        referenced.Should().NotContain("SmartPipe.Extensions.Csv");
        referenced.Should().NotContain("SmartPipe.Extensions.Dapper");
        referenced.Should().NotContain("SmartPipe.Extensions.EntityFrameworkCore");
        referenced.Should().NotContain("FastExpressionCompiler");
        referenced.Should().NotContain(name => name.StartsWith("Mapster.", StringComparison.Ordinal));
    }
}
