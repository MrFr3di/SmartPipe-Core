using SmartPipe.Extensions.Transforms;

#if INVOKE_RUC
using System.ComponentModel.DataAnnotations;
using SmartPipe.Core;

var transform = new ValidationTransform<AnnotatedModel>();
await transform.InitializeAsync();
var result = await transform.TransformAsync(
    ProcessingEnvelope<AnnotatedModel>.Create(new AnnotatedModel()));
if (result.IsSuccess)
    return 1;
#else
_ = typeof(ValidationTransform<>);
#endif
Console.WriteLine("CONSUMER_OK data-annotations-direct");
return 0;

#if INVOKE_RUC
internal sealed class AnnotatedModel
{
    [Required]
    public string? Name { get; init; }
}
#endif
