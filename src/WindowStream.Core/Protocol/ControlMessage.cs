using System.Text.Json.Serialization;

namespace WindowStream.Core.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), typeDiscriminator: "HELLO")]
[JsonDerivedType(typeof(ServerHelloMessage), typeDiscriminator: "SERVER_HELLO")]
[JsonDerivedType(typeof(StreamStartedMessage), typeDiscriminator: "STREAM_STARTED")]
[JsonDerivedType(typeof(StreamStoppedMessage), typeDiscriminator: "STREAM_STOPPED")]
[JsonDerivedType(typeof(RequestKeyframeMessage), typeDiscriminator: "REQUEST_KEYFRAME")]
[JsonDerivedType(typeof(HeartbeatMessage), typeDiscriminator: "HEARTBEAT")]
[JsonDerivedType(typeof(ErrorMessage), typeDiscriminator: "ERROR")]
public abstract record ControlMessage;
