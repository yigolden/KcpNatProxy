using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.InteropServices;

namespace KcpNatProxy.Client
{
    internal readonly struct KnpVirtualBusPeerConnectionInfo
    {
        private readonly IPEndPoint? _endPoint;
        private readonly long _accessSecret;

        [MemberNotNullWhen(false, nameof(EndPoint))]
        public bool IsEmpty => _endPoint is null;
        public IPEndPoint? EndPoint => _endPoint;
        public long AccessSecret => _accessSecret;

        public KnpVirtualBusPeerConnectionInfo(IPEndPoint endPoint, long accessSecret)
        {
            _endPoint = endPoint;
            _accessSecret = accessSecret;
        }

        public static bool TryParse(ReadOnlySpan<byte> buffer, out KnpVirtualBusPeerConnectionInfo info)
        {
            if (buffer.Length < 26)
            {
                info = default;
                return false;
            }
            long accessSecret = MemoryMarshal.Read<long>(buffer);
            IPAddress address;
            try
            {
                address = new IPAddress(buffer.Slice(8, 16));
            }
            catch (Exception)
            {
                info = default;
                return false;
            }
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(24, 2));

            info = new KnpVirtualBusPeerConnectionInfo(new IPEndPoint(address, port), accessSecret);
            return true;
        }
    }
}
