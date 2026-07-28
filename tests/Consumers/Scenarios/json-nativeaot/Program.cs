using System.Text.Json;
using System.Text.Json.Serialization;
using SmartPipe.Extensions.Transforms;

var info = ConsumerJsonContext.Default.ConsumerModel;
var transform = new JsonTransform<ConsumerModel, ConsumerModel>(info, info);
var json = JsonSerializer.Serialize(new ConsumerModel(7), info);
if (json.Length == 0 || transform is null) return 1;
Console.WriteLine("CONSUMER_OK json-nativeaot");
return 0;

internal sealed record ConsumerModel(int Value);
[JsonSerializable(typeof(ConsumerModel))]
internal sealed partial class ConsumerJsonContext : JsonSerializerContext;
