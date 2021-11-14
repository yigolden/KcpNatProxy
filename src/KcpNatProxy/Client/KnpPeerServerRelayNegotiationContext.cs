using System;
using System.Buffers.Binary;
using KcpNatProxy.NetworkConnection;

namespace KcpNatProxy.Client
{
    internal sealed class KnpPeerServerRelayNegotiationContext : IKcpConnectionNegotiationContext
    {
        private int _mtu;
        private int _step;

        public KnpPeerServerRelayNegotiationContext(int mtu)
        {
            _mtu = mtu;
        }

        public int? NegotiatedMtu => _mtu;

        public KcpConnectionNegotiationResult MoveNext(Span<byte> buffer)
        {
            if (_step == -1)
            {
                return KcpConnectionNegotiationResult.Succeeded;
            }
            switch (_step)
            {
                case 0:
                    buffer[0] = 0xe1;
                    buffer[1] = 0;
                    BinaryPrimitives.WriteInt16BigEndian(buffer.Slice(2), (short)_mtu);
                    return new KcpConnectionNegotiationResult(4);
                case 1:
                    buffer[0] = 0xe2;
                    buffer[1] = 0;
                    BinaryPrimitives.WriteInt16BigEndian(buffer.Slice(2), (short)_mtu);
                    return new KcpConnectionNegotiationResult(4, true);
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
            if (data[0] == 0xe1 && data[1] == 0)
            {
                int mtu = BinaryPrimitives.ReadInt16BigEndian(data.Slice(2));
                if (mtu < 512 || mtu > 12 * 1024)
                {
                    _step = -2;
                    return;
                }
                _mtu = Math.Min(_mtu, mtu);
                _step = 1;
            }
            else if (data[0] == 0xe2 && data[1] == 0)
            {
                int mtu = BinaryPrimitives.ReadInt16BigEndian(data.Slice(2));
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
