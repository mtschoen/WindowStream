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
[JsonDerivedType(typeof(ViewerReadyMessage), typeDiscriminator: "VIEWER_READY")]
[JsonDerivedType(typeof(KeyEventMessage), typeDiscriminator: "KEY_EVENT")]
[JsonDerivedType(typeof(WindowAddedMessage), typeDiscriminator: "WINDOW_ADDED")]
[JsonDerivedType(typeof(WindowRemovedMessage), typeDiscriminator: "WINDOW_REMOVED")]
[JsonDerivedType(typeof(WindowUpdatedMessage), typeDiscriminator: "WINDOW_UPDATED")]
[JsonDerivedType(typeof(WindowSnapshotMessage), typeDiscriminator: "WINDOW_SNAPSHOT")]
[JsonDerivedType(typeof(ListWindowsMessage), typeDiscriminator: "LIST_WINDOWS")]
[JsonDerivedType(typeof(OpenStreamMessage), typeDiscriminator: "OPEN_STREAM")]
[JsonDerivedType(typeof(CloseStreamMessage), typeDiscriminator: "CLOSE_STREAM")]
[JsonDerivedType(typeof(PauseStreamMessage), typeDiscriminator: "PAUSE_STREAM")]
[JsonDerivedType(typeof(ResumeStreamMessage), typeDiscriminator: "RESUME_STREAM")]
[JsonDerivedType(typeof(FocusWindowMessage), typeDiscriminator: "FOCUS_WINDOW")]
public abstract record ControlMessage;
