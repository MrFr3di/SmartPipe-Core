#nullable enable
#pragma warning disable CS0618 // These tests cover compatibility aliases.

using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class PipelineRuntimeOptionsCompatibilityTests
{
    public static TheoryData<PipelineOutputMode, PipelineOutputPolicy> EquivalentOutputSettings() =>
        new()
        {
            { PipelineOutputMode.EmitAll, PipelineOutputPolicy.EmitAll },
            { PipelineOutputMode.FailuresOnlyWhenSinkAttached, PipelineOutputPolicy.SuppressSuccessWhenSinkAttached },
            { PipelineOutputMode.SuppressWhenSinkAttached, PipelineOutputPolicy.SuppressAllWhenSinkAttached },
        };

    public static TheoryData<PipelineOutputMode, PipelineOutputPolicy> ConflictingOutputSettings() =>
        new()
        {
            { PipelineOutputMode.EmitAll, PipelineOutputPolicy.EmitFailuresOnly },
            { PipelineOutputMode.EmitAll, PipelineOutputPolicy.SuppressSuccessWhenSinkAttached },
            { PipelineOutputMode.EmitAll, PipelineOutputPolicy.SuppressAllWhenSinkAttached },
            { PipelineOutputMode.FailuresOnlyWhenSinkAttached, PipelineOutputPolicy.EmitAll },
            { PipelineOutputMode.FailuresOnlyWhenSinkAttached, PipelineOutputPolicy.EmitFailuresOnly },
            { PipelineOutputMode.FailuresOnlyWhenSinkAttached, PipelineOutputPolicy.SuppressAllWhenSinkAttached },
            { PipelineOutputMode.SuppressWhenSinkAttached, PipelineOutputPolicy.EmitAll },
            { PipelineOutputMode.SuppressWhenSinkAttached, PipelineOutputPolicy.EmitFailuresOnly },
            { PipelineOutputMode.SuppressWhenSinkAttached, PipelineOutputPolicy.SuppressSuccessWhenSinkAttached },
            { PipelineOutputMode.SuppressAll, PipelineOutputPolicy.EmitAll },
            { PipelineOutputMode.SuppressAll, PipelineOutputPolicy.EmitFailuresOnly },
            { PipelineOutputMode.SuppressAll, PipelineOutputPolicy.SuppressSuccessWhenSinkAttached },
            { PipelineOutputMode.SuppressAll, PipelineOutputPolicy.SuppressAllWhenSinkAttached },
        };

    [Theory]
    [MemberData(nameof(EquivalentOutputSettings))]
    public void Validate_AllowsEquivalentLegacyAndCurrentOutputSettings(
        PipelineOutputMode mode,
        PipelineOutputPolicy policy)
    {
        var options = new PipelineRuntimeOptions
        {
            OutputMode = mode,
            OutputPolicy = policy,
        };

        var act = () => options.Validate();

        act.Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(ConflictingOutputSettings))]
    public void Validate_RejectsConflictingLegacyAndCurrentOutputSettings(
        PipelineOutputMode mode,
        PipelineOutputPolicy policy)
    {
        var options = new PipelineRuntimeOptions
        {
            OutputMode = mode,
            OutputPolicy = policy,
        };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OutputMode*OutputPolicy*");
    }

    [Fact]
    public void Validate_AllowsDefaultOutputSettings()
    {
        var options = new PipelineRuntimeOptions();

        var act = () => options.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_AllowsOnlyLegacyOutputMode()
    {
        var options = new PipelineRuntimeOptions
        {
            OutputMode = PipelineOutputMode.SuppressAll,
        };

        var act = () => options.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_AllowsOnlyCurrentOutputPolicy()
    {
        var options = new PipelineRuntimeOptions
        {
            OutputPolicy = PipelineOutputPolicy.SuppressAllWhenSinkAttached,
        };

        var act = () => options.Validate();

        act.Should().NotThrow();
    }
}
