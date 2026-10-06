using System.ComponentModel.DataAnnotations;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;
using InvalidModel = SmartPipe.ConsumerScenarios.DataAnnotationsRuntime.InvalidModel;

await using var transform = new ValidationTransform<InvalidModel>();
await transform.InitializeAsync();
var result = await transform.TransformAsync(
    ProcessingEnvelope<InvalidModel>.Create(new InvalidModel()));
if (result.IsSuccess
    || result.Error is not { } error
    || error.Message != "name required"
    || error.Type != ErrorType.Permanent
    || error.Category != "Validation")
{
    return 1;
}

Console.WriteLine("CONSUMER_OK data-annotations-runtime");
return 0;

namespace SmartPipe.ConsumerScenarios.DataAnnotationsRuntime
{
    internal sealed class InvalidModel
    {
        [Required(ErrorMessage = "name required")]
        public string? Name { get; init; }
    }
}
