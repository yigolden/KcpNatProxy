using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KcpNatProxy.NetworkConnection;

namespace KcpNatProxy.Client
{
    public class KnpClientAuthenticator : IKcpConnectionNegotiationContext
    {
        private readonly int _mtu;
        private readonly byte[] _password;

        private int _step;
        private int _negotiatedMtu;

        private uint _serverId;
        private int _sessionId;
        private Guid _challenge;

        public uint ServerId => _serverId;
        public int SessionId => _sessionId;

        public KnpClientAuthenticator(int mtu, byte[] password)
        {
            if (mtu < 512 || mtu > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(mtu));
            }
            _mtu = mtu;
            _password = password;
        }

        public int? NegotiatedMtu => _negotiatedMtu;

        public KcpConnectionNegotiationResult MoveNext(Span<byte> data)
        {
            if (_step == -1)
            {
                return KcpConnectionNegotiationResult.Succeeded;
            }
            switch (_step)
            {
                case 0:
                    return WriteConnectRequest(data);
                case 2:
                    return WriteChapResult(data);
            }

            return KcpConnectionNegotiationResult.ContinuationRequired;
        }

        private KcpConnectionNegotiationResult WriteConnectRequest(Span<byte> data)
        {
            Debug.Assert(_step == 0);
            _step = 1;

            BinaryPrimitives.WriteUInt32BigEndian(data, 0x4B4E5032);
            data[4] = 1; // server-connect
            data[5] = _password.Length != 0 ? (byte)1 : (byte)0; // 0-none 1-chap 2-session secret
            BinaryPrimitives.WriteUInt16BigEndian(data.Slice(6), (ushort)_mtu);
            return new KcpConnectionNegotiationResult(8);
        }

        private KcpConnectionNegotiationResult WriteChapResult(Span<byte> data)
        {
            Debug.Assert(_step == 2);
            _step = 3;

            RandomNumberGenerator.Fill(data.Slice(0, 16));
            Span<byte> buffer = stackalloc byte[32 + _password.Length];
            MemoryMarshal.Write(buffer, ref _challenge);
            data.Slice(0, 16).CopyTo(buffer.Slice(16));
            _password.AsSpan().CopyTo(buffer.Slice(32));
            SHA256.HashData(buffer, data.Slice(16));

            return new KcpConnectionNegotiationResult(48, true);
        }

        public void PutNegotiationData(ReadOnlySpan<byte> data)
        {
            if (_step < 0)
            {
                return;
            }
            switch (_step)
            {
                case 1:
                    ReadConnectionResponse(data);
                    return;
                case 3:
                    _step = -1;
                    break;
            }

            _step = -2;
        }

        private void ReadConnectionResponse(ReadOnlySpan<byte> data)
        {
            Debug.Assert(_step == 1);

            if (data.Length < 12 || data[0] != 2)
            {
                _step = -2;
                return;
            }
            _negotiatedMtu = Math.Min(_mtu, BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2)));
            if (_negotiatedMtu < 512)
            {
                return;
            }
            _serverId = MemoryMarshal.Read<uint>(data.Slice(4));
            _sessionId = MemoryMarshal.Read<int>(data.Slice(8));
            if (data[1] == 0)
            {
                _step = -1;
                return;
            }
            if (data[1] != 1)
            {
                _step = -2;
                return;
            }

            if (data.Length < 28)
            {
                _step = -2;
                return;
            }

            _challenge = MemoryMarshal.Read<Guid>(data.Slice(12));
            _step = 2;
        }

    }
}
