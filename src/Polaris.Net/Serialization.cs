using System.Text.Json.Serialization;

namespace Polaris.Net;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LogEntry))]
[JsonSerializable(typeof(SignedTask))]
[JsonSerializable(typeof(Fleet))]
public sealed partial class NetJson : JsonSerializerContext;
