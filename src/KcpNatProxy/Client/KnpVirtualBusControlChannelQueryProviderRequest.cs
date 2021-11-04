using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace KcpNatProxy.Client
{
    internal readonly struct KnpVirtualBusControlChannelQueryProviderParameter : IKnpVirtualBusControlChannelRequestParameter<KnpVirtualBusProviderSessionBinding>
    {
        private readonly IKnpService _service;

        public IKnpService Service => _service;

        public KnpVirtualBusControlChannelQueryProviderParameter(IKnpService service)
        {
            _service = service;
        }

        public KnpVirtualBusControlChannelRequestBase<KnpVirtualBusProviderSessionBinding> CreateRequest()
            => new KnpVirtualBusControlChannelQueryProviderRequest(_service);
    }

    internal sealed class KnpVirtualBusControlChannelQueryProviderRequest : KnpVirtualBusControlChannelRequestBase<KnpVirtualBusProviderSessionBinding>
    {
        private readonly IKnpService _service;

        public KnpVirtualBusControlChannelQueryProviderRequest(IKnpService service)
        {
            _service = service;
        }

        public override bool AllowCache => true;
        public override bool IsExpired(long tick)
        {
            long? lastUpdateTimeTick = LastUpdateTimeTick;
            return lastUpdateTimeTick.HasValue && (long)((ulong)tick - (ulong)LastUpdateTimeTick.GetValueOrDefault()) > 20 * 1000;
        }

        public override bool CheckParameterMatches<TParameter>(TParameter parameter)
        {
            if (typeof(TParameter) != typeof(KnpVirtualBusControlChannelQueryProviderParameter))
            {
                return false;
            }
            var param = (KnpVirtualBusControlChannelQueryProviderParameter)(object)parameter;
            if (!ReferenceEquals(_service, param.Service))
            {
                return false;
            }
            return true;
        }

        public override int WriteRequest(Span<byte> buffer)
        {
            if (buffer.Length < 256)
            {
                return 0;
            }

            buffer[0] = 2;
            buffer[1] = (byte)_service.ServiceType;
            int bytesWritten = Encoding.UTF8.GetBytes(_service.ServiceName, buffer.Slice(3));
            Debug.Assert(bytesWritten <= 128);
            buffer[2] = (byte)bytesWritten;
            bytesWritten = 3 + bytesWritten;
            bytesWritten += _service.WriteParameters(buffer.Slice(bytesWritten));

            return bytesWritten;
        }

        public override KnpVirtualBusProviderSessionBinding ParseResponse(ReadOnlySpan<byte> data)
        {
            if (data.Length < 10)
            {
                return default;
            }

            int sessionId = BinaryPrimitives.ReadInt32BigEndian(data.Slice(2));
            int bindingId = BinaryPrimitives.ReadInt32BigEndian(data.Slice(6, 4));

            byte[] parameters = Array.Empty<byte>();
            if (data.Length > 10 && data.Length <= 18)
            {
                // limit to 8 bytes
                parameters = data.Slice(10, data.Length - 10).ToArray();
            }

            return new KnpVirtualBusProviderSessionBinding(sessionId, bindingId, parameters);
        }

    }
}
