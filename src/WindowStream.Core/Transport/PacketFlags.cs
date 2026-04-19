using System;

namespace WindowStream.Core.Transport;

[Flags]
public enum PacketFlags : byte
{
    None = 0x00,
    IdrFrame = 0x01,
    LastFragment = 0x02
}
