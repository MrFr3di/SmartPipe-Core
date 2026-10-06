using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineStageMetadataTests
{
    [Fact]
    public void Constructor_PreservesIdentityAndCopiesFailureOptions()
    {
        var timeout = new TimeoutPolicy { AttemptTimeout = TimeSpan.FromSeconds(2) };
        var options = new StageFailureOptions
        {
            Timeout = timeout,
            OnPermanentFailure = FailureAction.Skip,
        };

        var metadata = new PipelineStageMetadata(
            new PipelineStageKey("normalize"),
            " Normalize orders ",
            typeof(int),
            typeof(string),
            options);

        metadata.Key.Value.Should().Be("normalize");
        metadata.Name.Should().Be(" Normalize orders ");
        metadata.InputType.Should().Be(typeof(int));
        metadata.OutputType.Should().Be(typeof(string));
        metadata.FailureOptions.Should().NotBeSameAs(options);
        metadata.FailureOptions.Timeout.Should().NotBeSameAs(timeout);
        metadata.FailureOptions.Timeout!.AttemptTimeout.Should().Be(TimeSpan.FromSeconds(2));
        metadata.FailureOptions.OnPermanentFailure.Should().Be(FailureAction.Skip);
    }

    [Fact]
    public void Constructor_DefaultKey_IsRejected()
    {
        var act = () => new PipelineStageMetadata(
            default,
            "stage",
            typeof(int),
            typeof(int),
            StageFailureOptions.Default);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WhitespaceName_IsRejected()
    {
        var act = () => new PipelineStageMetadata(
            new PipelineStageKey("stage"),
            " \t",
            typeof(int),
            typeof(int),
            StageFailureOptions.Default);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullReferences_AreRejected()
    {
        var key = new PipelineStageKey("stage");

        Action nullInput = () => new PipelineStageMetadata(
            key, "stage", null!, typeof(int), StageFailureOptions.Default);
        Action nullOutput = () => new PipelineStageMetadata(
            key, "stage", typeof(int), null!, StageFailureOptions.Default);
        Action nullOptions = () => new PipelineStageMetadata(
            key, "stage", typeof(int), typeof(int), (StageFailureOptions)null!);

        nullInput.Should().Throw<ArgumentNullException>();
        nullOutput.Should().Throw<ArgumentNullException>();
        nullOptions.Should().Throw<ArgumentNullException>();
    }
}
