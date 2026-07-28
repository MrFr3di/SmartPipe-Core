using System.Text.Json;
using System.Text.Json.Serialization;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

var model = new ConsumerModel(42);
var json = JsonSerializer.Serialize(model, ConsumerJsonContext.Default.ConsumerModel);
var transformed = await new JsonTransform<ConsumerModel, ConsumerModel>(ConsumerJsonContext.Default.ConsumerModel, ConsumerJsonContext.Default.ConsumerModel)
    .TransformAsync(ProcessingEnvelope<ConsumerModel>.Create(model));
var roundTrip = JsonSerializer.Deserialize(json, ConsumerJsonContext.Default.ConsumerModel);
if (roundTrip?.Value != 42 || transformed.IsSuccess is false || transformed.Value?.Value != 42) return 1;
Console.WriteLine("CONSUMER_OK json-direct");
return 0;

internal sealed record ConsumerModel(int Value);
[JsonSerializable(typeof(ConsumerModel))]
internal sealed partial class ConsumerJsonContext : JsonSerializerContext;
