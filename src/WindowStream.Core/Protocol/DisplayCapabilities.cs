using System.Collections.Generic;

namespace WindowStream.Core.Protocol;

public sealed record DisplayCapabilities(
    int MaximumWidth,
    int MaximumHeight,
    IReadOnlyList<string> SupportedCodecs);
