using System;
using System.Buffers.Binary;

namespace KcpNatProxy.Client
{
    internal readonly struct KnpVirtualBusControlChannelConnectPeerParameter : IKnpVirtualBusControlChannelRequestParameter<KnpVirtualBusPeerConnectionInfo>
    {
        private readonly int _sessionId;

        public int SessionId => _sessionId;

        public KnpVirtualBusControlChannelConnectPeerParameter(int sessionId)
        {
            _sessionId = sessionId;
        }

        public KnpVirtualBusControlChannelRequestBase<KnpVirtualBusPeerConnectionInfo> CreateRequest()
            => new KnpVirtualBusControlChannelConnectPeerRequest(_sessionId);
    }

    internal sealed class KnpVirtualBusControlChannelConnectPeerRequest : KnpVirtualBusControlChannelRequestBase<KnpVirtualBusPeerConnectionInfo>
    {
        private readonly int _sessionId;
        private bool _requested;

        public override bool IsExpired(DateTime utcNow) => _requested;

        public KnpVirtualBusControlChannelConnectPeerRequest(int sessionId)
        {
            _sessionId = sessionId;
        }

        public override bool CheckParameterMatches<TParameter>(TParameter parameter)
        {
            if (typeof(TParameter) != typeof(KnpVirtualBusControlChannelConnectPeerParameter))
            {
                return false;
            }
            var param = (KnpVirtualBusControlChannelConnectPeerParameter)(object)parameter;
            return _sessionId == param.SessionId;
        }

        public override int WriteRequest(Span<byte> buffer)
        {
            _requested = true;
            if (buffer.Length < 5)
            {
                return 0;
            }
            buffer[0] = 3;
            BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(1), _sessionId);
            return 5;
        }

        public override KnpVirtualBusPeerConnectionInfo ParseResponse(ReadOnlySpan<byte> data)
        {
            if (data.Length < 28 || data[0] != 0)
            {
                return default;
            }
            _ = KnpVirtualBusPeerConnectionInfo.TryParse(data.Slice(2, 26), out KnpVirtualBusPeerConnectionInfo connectionInfo);
            return connectionInfo;
        }
    }
}
