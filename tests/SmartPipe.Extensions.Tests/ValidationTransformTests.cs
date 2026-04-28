using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Extensions.Tests;

public class ValidationTransformTests
{
    public class TestValidated { [Required] public string Name { get; set; } = ""; [Range(1, 100)] public int Age { get; set; } }

    [Fact]
    public async Task ValidObject_ShouldPass()
    {
        var v = new ValidationTransform<TestValidated>();
        var result = await v.TransformAsync(new ProcessingContext<TestValidated>(new TestValidated { Name = "Alice", Age = 30 }));
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task InvalidObject_ShouldFail()
    {
        var v = new ValidationTransform<TestValidated>();
        var result = await v.TransformAsync(new ProcessingContext<TestValidated>(new TestValidated { Name = "", Age = 0 }));
        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Category.Should().Be("Validation");
    }

    [Fact]
    public async Task CustomRule_ShouldApply()
    {
        var v = new ValidationTransform<TestValidated>().Require(x => x.Age >= 18, "Must be 18+");
        var result = await v.TransformAsync(new ProcessingContext<TestValidated>(new TestValidated { Name = "Bob", Age = 16 }));
        result.IsSuccess.Should().BeFalse();
        result.Error!.Value.Message.Should().Contain("Must be 18+");
    }
}
