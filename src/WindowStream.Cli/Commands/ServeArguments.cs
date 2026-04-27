namespace WindowStream.Cli.Commands;

/// <summary>
/// Arguments for the v2 <c>serve</c> command. Parameterless — the viewer
/// selects the window remotely via OPEN_STREAM after connecting.
/// </summary>
public sealed record ServeArguments();
