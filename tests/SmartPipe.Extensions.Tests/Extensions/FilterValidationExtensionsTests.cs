#nullable enable
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;
using Xunit;

namespace SmartPipe.Extensions.Tests.Extensions;

public class FilterValidationExtensionsTests
{
    [Fact]
    public void ToFilter_ConvertsValidationTransform_ToFilterTransform()
    {
        var validator = new ValidationTransform<string>();
        validator.Require(x => x.Length > 0, "Empty");
        
        var filter = validator.ToFilter();
        
        Assert.NotNull(filter);
        Assert.IsType<FilterTransform<string>>(filter);
    }

    [Fact]
    public async Task ToFilter_FiltersOutInvalidItems()
    {
        var validator = new ValidationTransform<string>();
        validator.Require(x => !string.IsNullOrEmpty(x), "Empty");
        
        var filter = validator.ToFilter();
        var context = new ProcessingContext<string>("test");
        
        var result = await filter.TransformAsync(context);
        
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ToFilter_WithCustomPredicate_FiltersCorrectly()
    {
        var validator = new ValidationTransform<string>();
        validator.Require(x => !string.IsNullOrEmpty(x), "Empty");
        
        var filter = validator.ToFilter();
        var context = new ProcessingContext<string>("test");
        
        var result = await filter.TransformAsync(context);
        
        Assert.True(result.IsSuccess);
    }
}
