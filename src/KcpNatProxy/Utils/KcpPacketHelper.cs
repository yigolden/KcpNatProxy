using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace KcpNatProxy
{
    internal static class KcpPacketHelper
    {
        internal static bool IsFirstPacket(ReadOnlySpan<byte> packet)
        {
            if (packet.Length <= 20)
            {
                return false;
            }
            if (packet[0] != 81)
            {
                return false;
            }
            if (MemoryMarshal.Read<long>(packet.Slice(8)) != 0)
            {
                return false;
            }
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(16));
            if (length > (packet.Length - 20))
            {
                return false;
            }
            return true;
        }

        internal static bool IsFirstPacket(ReadOnlySpan<byte> packet, int conversationId)
        {
            if (packet.Length <= 24)
            {
                return false;
            }
            if (BinaryPrimitives.ReadInt32LittleEndian(packet) != conversationId)
            {
                return false;
            }
            if (packet[4] != 81)
            {
                return false;
            }
            if (MemoryMarshal.Read<long>(packet.Slice(12)) != 0)
            {
                return false;
            }
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(20));
            if (length > (packet.Length - 24))
            {
                return false;
            }
            return true;
        }
    }
}
