using System;

namespace WindowStream.Core.Transport;

public readonly record struct FragmentedPacket(PacketHeader Header, ReadOnlyMemory<byte> Payload);
