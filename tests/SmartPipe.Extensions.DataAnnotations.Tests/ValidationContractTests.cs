using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.DataAnnotations.Tests;

public sealed class ValidationContractTests
{
    private const string ReflectionContract =
        "Reflection-based DataAnnotations validation is not trimming-safe.";

    [Fact]
    public async Task ValidationIsNonRecursive()
    {
        var validation = new ValidationTransform<Outer>()
            .Require(static value => value.Inner is not null, "inner required");

        var result = await validation.TransformAsync(
            ProcessingEnvelope<Outer>.Create(new Outer
            {
                Name = "outer",
                Inner = new Inner(),
            }), TestContext.Current.CancellationToken);

        // Catches replacing Validator.TryValidateObject with a recursive graph walker.
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ValidationAggregatesAttributeAndRuleErrorsInLegacyOrder()
    {
        var validation = new ValidationTransform<Outer>()
            .Require(static _ => false, "custom rule");

        var result = await validation.TransformAsync(
            ProcessingEnvelope<Outer>.Create(new Outer
            {
                Inner = new Inner(),
            }), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);

        // Catches dropping an attribute error, changing aggregation order, or losing custom rules.
        Assert.Equal("outer name; custom rule", result.Error!.Value.Message);
        Assert.Equal(ErrorType.Permanent, result.Error.Value.Type);
        Assert.Equal("Validation", result.Error.Value.Category);
    }

    [Fact]
    public async Task ValidationRulesFreezeAfterInitialization()
    {
        var validation = new ValidationTransform<Outer>();
        await validation.InitializeAsync(TestContext.Current.CancellationToken);

        // Catches allowing configuration mutation after the snapshot is published.
        Assert.Throws<InvalidOperationException>(() =>
        {
            validation.Require(static _ => true, "late rule");
        });
    }

    [Fact]
    public async Task ValidationRulesFreezeBeforeFirstExecution()
    {
        var validation = new ValidationTransform<Outer>();
        await validation.TransformAsync(
            ProcessingEnvelope<Outer>.Create(new Outer { Name = "outer" }),
            TestContext.Current.CancellationToken);

        // Catches freezing only from InitializeAsync and leaving the first execution mutable.
        Assert.Throws<InvalidOperationException>(() =>
        {
            validation.Require(static _ => true, "late rule");
        });
    }

    [Fact]
    public async Task ValidationTransformPropagatesCancellationToken()
    {
        var validation = new ValidationTransform<Outer>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Catches ignoring the transform cancellation token before reflection starts.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            validation.TransformAsync(
                ProcessingEnvelope<Outer>.Create(new Outer()), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task ToFilterPropagatesCancellationToken()
    {
        var filter = new ValidationTransform<Outer>().ToFilter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Catches a bridge that invokes ValidationTransform without forwarding the token.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            filter.TransformAsync(
                ProcessingEnvelope<Outer>.Create(new Outer()), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task ToFilterConvertsValidationFailureToFilteredResult()
    {
        var filter = new ValidationTransform<Outer>()
            .Require(static _ => false, "custom rule")
            .ToFilter();

        var result = await filter.TransformAsync(
            ProcessingEnvelope<Outer>.Create(new Outer { Name = "outer" }),
            TestContext.Current.CancellationToken);

        // Catches returning a failed validation result directly instead of the filter terminal state.
        Assert.Equal(StageResultKind.Filtered, result.Kind);
    }

    [Fact]
    public void ReflectionValidationPathsCarryExactTrimmingContract()
    {
        var transformMethod = typeof(ValidationTransform<Outer>)
            .GetMethod(
                nameof(ValidationTransform<Outer>.TransformAsync),
                [typeof(ProcessingEnvelope<Outer>), typeof(CancellationToken)]);
        var bridgeMethod = typeof(FilterValidationExtensions)
            .GetMethod(nameof(FilterValidationExtensions.ToFilter))!;

        var transformContract = transformMethod?.GetCustomAttribute<RequiresUnreferencedCodeAttribute>();
        var bridgeContract = bridgeMethod.GetCustomAttribute<RequiresUnreferencedCodeAttribute>();

        // Catches hiding IL2026 with suppression or annotating a non-invoking member.
        Assert.NotNull(transformContract);
        Assert.NotNull(bridgeContract);
        Assert.Equal(ReflectionContract, transformContract!.Message);
        Assert.Equal(ReflectionContract, bridgeContract!.Message);
    }

    private sealed class Outer
    {
        [Required(ErrorMessage = "outer name")]
        public string? Name { get; init; }

        public Inner? Inner { get; init; }
    }

    private sealed class Inner
    {
        [Required(ErrorMessage = "inner name")]
        public string? Name { get; init; }
    }
}
