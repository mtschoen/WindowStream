using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowStream.Core.Protocol;

public sealed record DisplayCapabilities(
    int MaximumWidth,
    int MaximumHeight,
    IReadOnlyList<string> SupportedCodecs)
{
    public bool Equals(DisplayCapabilities? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return MaximumWidth == other.MaximumWidth
            && MaximumHeight == other.MaximumHeight
            && SupportedCodecs.SequenceEqual(other.SupportedCodecs);
    }

    public override int GetHashCode()
    {
        HashCode hashCode = new();
        hashCode.Add(MaximumWidth);
        hashCode.Add(MaximumHeight);
        foreach (string codec in SupportedCodecs)
        {
            hashCode.Add(codec);
        }
        return hashCode.ToHashCode();
    }
}
