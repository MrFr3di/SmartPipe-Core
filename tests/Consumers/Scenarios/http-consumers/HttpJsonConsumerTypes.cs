using System.Text.Json.Serialization;

namespace SmartPipe.Consumers.Http;

internal sealed record ConsumerOrder(int Id, string Name);

[JsonSerializable(typeof(ConsumerOrder))]
internal sealed partial class ConsumerJsonContext : JsonSerializerContext;
