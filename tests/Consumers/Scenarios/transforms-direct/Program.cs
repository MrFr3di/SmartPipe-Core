using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

var transform = new RuleValidationTransform<int>().Require(static value => value == 42, "unexpected value");
await transform.InitializeAsync();
var result = await transform.TransformAsync(ProcessingEnvelope<int>.Create(42));
if (!result.IsSuccess || result.Value != 42)
    return 1;

Console.WriteLine("CONSUMER_OK transforms-direct");
return 0;
