using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms.Tests;

public sealed class RuleValidationTransformTests
{
    [Fact]
    public async Task TransformAsync_ReturnsExplicitOrderedRuleFailures()
    {
        var transform = new RuleValidationTransform<int>()
            .Require(static value => value > 0, "positive")
            .Require(static value => value % 2 == 0, "even");
        await transform.InitializeAsync(TestContext.Current.CancellationToken);

        StageResult<int> result = await transform.TransformAsync(
            ProcessingEnvelope<int>.Create(-1), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        SmartPipeError error = result.Error!.Value;
        Assert.Equal(ErrorType.Permanent, error.Type);
        Assert.Equal("Validation", error.Category);
        Assert.Equal("positive; even", error.Message);
    }

    [Fact]
    public async Task InitializeAsync_FreezesRulesAndIsIdempotent()
    {
        var transform = new RuleValidationTransform<int>()
            .Require(static value => value > 0, "positive");

        CancellationToken token = TestContext.Current.CancellationToken;
        await Task.WhenAll(transform.InitializeAsync(token).AsTask(), transform.InitializeAsync(token).AsTask());

        Assert.Throws<InvalidOperationException>(() => transform.Require(static value => value < 10, "small"));
        Assert.True((await transform.TransformAsync(ProcessingEnvelope<int>.Create(1), token)).IsSuccess);
    }

    [Fact]
    public void Require_RejectsInvalidRuleDefinition()
    {
        var transform = new RuleValidationTransform<int>();

        Assert.Throws<ArgumentNullException>(() => transform.Require(null!, "message"));
        Assert.Throws<ArgumentException>(() => transform.Require(static _ => true, ""));
    }
}
