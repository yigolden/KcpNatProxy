using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using KcpNatProxy.NetworkConnection;

namespace KcpNatProxy.Server
{
    public class KnpServerAuthenticator : IKcpConnectionNegotiationContext
    {
        private uint _serverId;
        private readonly int _mtu;
        private readonly byte[] _password;

        private int _step;
        private int _negotiatedMtu;

        private int _sessionId;
        private KnpChapChallenge _challenge;

        public KnpServerAuthenticator(uint serverId, int mtu, byte[] password, int sessionId)
        {
            _serverId = serverId;
            _mtu = mtu;
            _password = password;
            _sessionId = sessionId;
        }

        public int? NegotiatedMtu => _negotiatedMtu;

        public KcpConnectionNegotiationResult MoveNext(Span<byte> data)
        {
            if (_step == -1)
            {
                return KcpConnectionNegotiationResult.Succeeded;
            }
            if (_step == -2)
            {
                return KcpConnectionNegotiationResult.Failed;
            }

            switch (_step)
            {
                case 0:
                    return KcpConnectionNegotiationResult.ContinuationRequired;
                case 1: // skip authentication
                    data[0] = 2;
                    data[1] = 0;
                    BinaryPrimitives.WriteUInt16BigEndian(data.Slice(2), (ushort)_negotiatedMtu);
                    MemoryMarshal.Write(data.Slice(4), ref _serverId);
                    MemoryMarshal.Write(data.Slice(8), ref _sessionId);
                    _step = -1;
                    return new KcpConnectionNegotiationResult(12, true);
                case 2: // chap
                    _challenge = KnpChapChallenge.Create();

                    data[0] = 2;
                    data[1] = 1;
                    BinaryPrimitives.WriteUInt16BigEndian(data.Slice(2), (ushort)_negotiatedMtu);
                    MemoryMarshal.Write(data.Slice(4), ref _serverId);
                    MemoryMarshal.Write(data.Slice(8), ref _sessionId);
                    _challenge.Write(data.Slice(12, 16));
                    _step = 3;
                    return new KcpConnectionNegotiationResult(28);
            }

            return KcpConnectionNegotiationResult.ContinuationRequired;
        }


        public void PutNegotiationData(ReadOnlySpan<byte> data)
        {
            if (_step < 0)
            {
                return;
            }
            switch (_step)
            {
                case 0:
                    ReadConnectionRequest(data);
                    return;
                case 3: // chap result
                    if (data.Length < 48)
                    {
                        _step = -2;
                    }
                    else if (_challenge.Validate(data.Slice(0, 16), _password, data.Slice(16, 32)))
                    {
                        _step = -1;
                    }
                    else
                    {
                        _step = -2;
                    }
                    return;
            }

            _step = -2;
        }

        private void ReadConnectionRequest(ReadOnlySpan<byte> data)
        {
            if (data.Length < 8)
            {
                _step = -2;
                return;
            }
            if (BinaryPrimitives.ReadUInt32BigEndian(data) != 0x4B4E5032)
            {
                _step = -2;
                return;
            }
            data = data.Slice(4);

            if (data[0] != 1)
            {
                _step = -2;
                return;
            }
            _negotiatedMtu = Math.Min(_mtu, BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2)));
            if (_negotiatedMtu < 512)
            {
                _step = -2;
                return;
            }
            if (_password.Length == 0)
            {
                // skip authentication
                _step = 1;
                return;
            }
            int authType = data[1];

            // try to use CHAP
            do
            {
                if ((authType & 1) == 0)
                {
                    break;
                }

                _step = 2;
                return;
            } while (false);

            _step = -2;
            return;
        }

    }
}
