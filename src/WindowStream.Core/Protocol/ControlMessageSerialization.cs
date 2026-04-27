using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowStream.Core.Protocol;

public static class ControlMessageSerialization
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new ProtocolErrorCodeConverter(), new StreamStoppedReasonConverter() }
    };

    public static string Serialize(ControlMessage message)
    {
        return JsonSerializer.Serialize(message, Options);
    }

    public static ControlMessage Deserialize(string payload)
    {
        try
        {
            ControlMessage? decoded = JsonSerializer.Deserialize<ControlMessage>(payload, Options);
            if (decoded is null)
            {
                throw new MalformedMessageException("payload deserialized to null");
            }
            return decoded;
        }
        catch (JsonException exception)
        {
            throw new MalformedMessageException($"could not parse control message: {exception.Message}", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new MalformedMessageException($"unsupported control message discriminator: {exception.Message}", exception);
        }
    }

    private sealed class ProtocolErrorCodeConverter : JsonConverter<ProtocolErrorCode>
    {
        public override ProtocolErrorCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? wireName = reader.GetString();
            if (wireName is null)
            {
                throw new JsonException("null is not a valid protocol error code");
            }
            return ProtocolErrorCodeNames.Parse(wireName);
        }

        public override void Write(Utf8JsonWriter writer, ProtocolErrorCode value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(ProtocolErrorCodeNames.ToWireName(value));
        }
    }

    private sealed class StreamStoppedReasonConverter : JsonConverter<StreamStoppedReason>
    {
        public override StreamStoppedReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? wireName = reader.GetString();
            if (wireName is null)
            {
                throw new JsonException("null is not a valid stream-stopped reason");
            }
            return StreamStoppedReasonNames.Parse(wireName);
        }

        public override void Write(Utf8JsonWriter writer, StreamStoppedReason value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(StreamStoppedReasonNames.ToWireName(value));
        }
    }
}
