using WindowStream.Core.Capture;

namespace WindowStream.Cli.Commands;

public sealed record ServeArguments(WindowHandle? Handle, string? TitlePattern);
