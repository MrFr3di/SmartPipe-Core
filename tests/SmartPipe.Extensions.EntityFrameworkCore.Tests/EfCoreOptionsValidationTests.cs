#nullable enable

using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Extensions.EntityFrameworkCore.Tests;

public sealed class EfCoreOptionsValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0001name")]
    [InlineData("bad\nname")]
    public void QuerySource_RejectsInvalidOperationNamesBeforeAnyContextCreation(string operationName)
    {
        var factory = new RecordingContextFactory();

        var act = () => EfCorePipelineComponents.QuerySource<TestDbContext, TestRow>(
            factory.CreateAsync,
            (context, activation) => new RecordingQueryable<TestRow>([], "invalid-name"),
            new EfCoreQueryOptions { OperationName = operationName });

        act.Should().Throw<ArgumentException>();
        factory.CallCount.Should().Be(0);
    }

    [Fact]
    public void QuerySource_RejectsAnOperationNameLongerThanSixtyFourCharacters()
    {
        var factory = new RecordingContextFactory();

        var act = () => EfCorePipelineComponents.QuerySource<TestDbContext, TestRow>(
            factory.CreateAsync,
            (context, activation) => new RecordingQueryable<TestRow>([], "long-name"),
            new EfCoreQueryOptions { OperationName = new string('a', 65) });

        act.Should().Throw<ArgumentException>();
        factory.CallCount.Should().Be(0);
    }

    [Fact]
    public void QuerySource_AcceptsAnOperationNameOfExactlySixtyFourCharacters()
    {
        var factory = new RecordingContextFactory();

        var component = EfCorePipelineComponents.QuerySource<TestDbContext, TestRow>(
            factory.CreateAsync,
            (context, activation) => new RecordingQueryable<TestRow>([], "max-name"),
            new EfCoreQueryOptions { OperationName = new string('a', 64) });

        component.Should().NotBeNull();
        factory.CallCount.Should().Be(0);
    }

    [Fact]
    public void QuerySource_RejectsAnUndefinedTrackingMode()
    {
        var factory = new RecordingContextFactory();

        var act = () => EfCorePipelineComponents.QuerySource<TestDbContext, TestRow>(
            factory.CreateAsync,
            (context, activation) => new RecordingQueryable<TestRow>([], "undefined-mode"),
            new EfCoreQueryOptions { TrackingMode = (EfCoreQueryTrackingMode)99 });

        act.Should().Throw<ArgumentOutOfRangeException>();
        factory.CallCount.Should().Be(0);
    }

    [Fact]
    public void Components_RejectNullArgumentsAtComposition()
    {
        var factory = new RecordingContextFactory();

        var nullFactory = () => EfCorePipelineComponents.QuerySource<TestDbContext, TestRow>(
            (Func<PipelineActivationContext, CancellationToken, ValueTask<TestDbContext>>)null!,
            (context, activation) => new RecordingQueryable<TestRow>([], "null-factory"),
            new EfCoreQueryOptions());
        var nullQueryFactory = () => EfCorePipelineComponents.QuerySource<TestDbContext, TestRow>(
            factory.CreateAsync,
            null!,
            new EfCoreQueryOptions());
        var nullOptions = () => EfCorePipelineComponents.QuerySource<TestDbContext, TestRow>(
            factory.CreateAsync,
            (context, activation) => new RecordingQueryable<TestRow>([], "null-options"),
            (EfCoreQueryOptions)null!);
        var nullCompiledQuery = () => EfCorePipelineComponents.CompiledQuerySource<TestDbContext, int>(
            factory.CreateAsync,
            null!,
            new EfCoreCompiledQueryOptions());
        var nullCompiledOptions = () => EfCorePipelineComponents.CompiledQuerySource<TestDbContext, int>(
            factory.CreateAsync,
            (context, activation, ct) => Empty<int>(),
            (EfCoreCompiledQueryOptions)null!);

        nullFactory.Should().Throw<ArgumentNullException>();
        nullQueryFactory.Should().Throw<ArgumentNullException>();
        nullOptions.Should().Throw<ArgumentNullException>();
        nullCompiledQuery.Should().Throw<ArgumentNullException>();
        nullCompiledOptions.Should().Throw<ArgumentNullException>();
        factory.CallCount.Should().Be(0);
    }

    [Fact]
    public void Options_AreInitOnlySoAComposedDescriptorCanNeverBeChangedLater()
    {
        var operationNameSetter = typeof(EfCoreQueryOptions)
            .GetProperty(nameof(EfCoreQueryOptions.OperationName))!
            .SetMethod;
        var trackingModeSetter = typeof(EfCoreQueryOptions)
            .GetProperty(nameof(EfCoreQueryOptions.TrackingMode))!
            .SetMethod;
        var compiledSetter = typeof(EfCoreCompiledQueryOptions)
            .GetProperty(nameof(EfCoreCompiledQueryOptions.OperationName))!
            .SetMethod;

        operationNameSetter.Should().NotBeNull();
        trackingModeSetter.Should().NotBeNull();
        compiledSetter.Should().NotBeNull();
        operationNameSetter!.ReturnParameter.GetRequiredCustomModifiers()
            .Should().Contain(typeof(System.Runtime.CompilerServices.IsExternalInit));
        trackingModeSetter!.ReturnParameter.GetRequiredCustomModifiers()
            .Should().Contain(typeof(System.Runtime.CompilerServices.IsExternalInit));
        compiledSetter!.ReturnParameter.GetRequiredCustomModifiers()
            .Should().Contain(typeof(System.Runtime.CompilerServices.IsExternalInit));
    }

    private static async IAsyncEnumerable<T> Empty<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
