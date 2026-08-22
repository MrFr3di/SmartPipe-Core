using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Transforms.Tests;

public sealed class TransformsContractTests
{
    [Fact]
    public async Task FilterAndRulesUseTheRevisedContracts()
    {
        var expected = new CancellationTokenSource().Token;
        var observed = CancellationToken.None;
        var filter = new FilterTransform<int>((_, token) =>
        {
            observed = token;
            return ValueTask.FromResult(true);
        });
        var rules = new RuleValidationTransform<int>()
            .Require(static value => value > 0, "positive");

        await filter.TransformAsync(ProcessingEnvelope<int>.Create(1), expected);
        await rules.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, observed);
        Assert.Throws<InvalidOperationException>((Action)(() =>
            rules.Require(static value => value < 10, "less than ten")));
    }
}
