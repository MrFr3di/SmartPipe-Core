using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartPipe.Extensions;

[JsonSerializable(typeof(JsonElement))]
internal sealed partial class JsonInfrastructureContext : JsonSerializerContext;
