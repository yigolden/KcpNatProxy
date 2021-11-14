using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KcpNatProxy.NetworkConnection;

namespace KcpNatProxy.Client
{
    internal sealed class KnpPeerServerReflexConnectionNegotiationContext : IKcpConnectionNegotiationContext
    {
        private int _mtu;
        private readonly uint _serverId;
        private readonly int _remoteSessionId;
        private readonly int _sessionId;
        private readonly long _accessSecret;

        private int _step;

        public KnpPeerServerReflexConnectionNegotiationContext(int mtu, uint serverId, int remoteSessionId, int sessionId, long accessSecret)
        {
            _mtu = mtu;
            _serverId = serverId;
            _remoteSessionId = remoteSessionId;
            _sessionId = sessionId;
            _accessSecret = accessSecret;
        }

        public int? NegotiatedMtu => _mtu;

        public KcpConnectionNegotiationResult MoveNext(Span<byte> data)
        {
            if (_step == -1)
            {
                return KcpConnectionNegotiationResult.Succeeded;
            }
            switch (_step)
            {
                case 0:
                    BinaryPrimitives.WriteUInt32BigEndian(data, 0xDE4B4E50);
                    BinaryPrimitives.WriteUInt32BigEndian(data.Slice(4), _serverId);
                    BinaryPrimitives.WriteInt32BigEndian(data.Slice(8), _remoteSessionId);
                    BinaryPrimitives.WriteInt32BigEndian(data.Slice(12), _sessionId);
                    MemoryMarshal.Write(data.Slice(16), ref Unsafe.AsRef(in _accessSecret));
                    BinaryPrimitives.WriteInt16BigEndian(data.Slice(24), (short)_mtu);
                    return new KcpConnectionNegotiationResult(26);
                case 1:
                    BinaryPrimitives.WriteUInt32BigEndian(data, 0xDF4B4E50);
                    BinaryPrimitives.WriteInt16BigEndian(data.Slice(4), (short)_mtu);
                    return new KcpConnectionNegotiationResult(6, true);
            }

            return KcpConnectionNegotiationResult.Failed;
        }

        public void PutNegotiationData(ReadOnlySpan<byte> data)
        {
            if (_step < 0)
            {
                return;
            }
            if (data.Length < 4)
            {
                _step = -2;
                return;
            }
            uint flag = BinaryPrimitives.ReadUInt32BigEndian(data);
            if (flag == 0xDE4B4E50)
            {
                if (data.Length < 26)
                {
                    _step = -2;
                    return;
                }
                if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4)) != _serverId
                    || BinaryPrimitives.ReadInt32BigEndian(data.Slice(8)) != _sessionId
                    || BinaryPrimitives.ReadInt32BigEndian(data.Slice(12)) != _remoteSessionId
                    || MemoryMarshal.Read<long>(data.Slice(16)) != _accessSecret)
                {
                    _step = -2;
                    return;
                }
                int mtu = BinaryPrimitives.ReadInt16BigEndian(data.Slice(24));
                if (mtu < 512 || mtu > 12 * 1024)
                {
                    _step = -2;
                    return;
                }
                _mtu = Math.Min(_mtu, mtu);
                _step = 1;
            }
            else if (flag == 0xDF4B4E50)
            {
                int mtu = BinaryPrimitives.ReadInt16BigEndian(data.Slice(4));
                if (mtu < 512 || mtu > 12 * 1024)
                {
                    _step = -2;
                    return;
                }
                _mtu = Math.Min(_mtu, mtu);
                _step = -1;
            }
            else
            {
                _step = -2;
            }
        }
    }
}
