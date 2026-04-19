using System.Text.Json.Serialization;

namespace WindowStream.Core.Protocol;

public sealed record ErrorMessage(
    [property: JsonPropertyName("code")] ProtocolErrorCode Code,
    [property: JsonPropertyName("message")] string Message) : ControlMessage;
