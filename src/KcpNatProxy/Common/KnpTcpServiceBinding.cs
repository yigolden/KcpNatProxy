using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy
{
    internal sealed class KnpTcpServiceBinding : IKnpServiceBinding, IKnpForwardHost
    {
        private readonly string _serviceName;
        private readonly IKnpConnectionHost _host;
        private readonly KnpRentedInt32 _bindingId;
        private readonly KnpTcpKcpParameters _parameters;
        private readonly KnpTcpKcpRemoteParameters _remoteParameters;
        private readonly KnpServiceBindingRegistration _registration;

        private readonly ConcurrentDictionary<int, IKnpForwardSession> _forwards = new();
        private int _disposed;

        public string ServiceName => _serviceName;
        public int BindingId => _bindingId.Value;
        public int Mss => _host.Mtu - 8;

        public KnpTcpServiceBinding(string serviceName, IKnpConnectionHost host, KnpRentedInt32 bindingId, IKnpServiceBindingHolder holder, KnpTcpKcpParameters parameters, KnpTcpKcpRemoteParameters remoteParameters)
        {
            _serviceName = serviceName;
            _host = host;
            _bindingId = bindingId;
            _parameters = parameters;
            _remoteParameters = remoteParameters;
            _registration = new KnpServiceBindingRegistration(holder, this);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _registration.Dispose();
                _bindingId.Dispose();

                KnpCollectionHelper.ClearAndDispose(_forwards);
            }
        }

        public void Run(Socket socket, KnpRentedInt32 forwardId)
        {
            if (_disposed != 0)
            {
                socket.Dispose();
                forwardId.Dispose();
                return;
            }

            var forwardSession = new KnpTcpServiceForwardSession(this, _host.BufferPool, forwardId, socket, _parameters, _remoteParameters, _host.Logger);
            if (!_forwards.TryAdd(forwardId.Value, forwardSession))
            {
                forwardSession.Dispose();
                return;
            }

            if (_disposed != 0)
            {
                if (_forwards.TryRemove(new KeyValuePair<int, IKnpForwardSession>(forwardId.Value, forwardSession)))
                {
                    forwardSession.Dispose();
                }
                return;
            }

            Span<byte> buffer = stackalloc byte[8];
            _bindingId.WriteBigEndian(buffer);
            forwardId.Write(buffer.Slice(4));

            if (!_host.TryRegister(MemoryMarshal.Read<long>(buffer), forwardSession))
            {
                if (_forwards.TryRemove(new KeyValuePair<int, IKnpForwardSession>(forwardId.Value, forwardSession)))
                {
                    forwardSession.Dispose();
                }
                return;
            }

            forwardSession.Start();
        }

        ValueTask IKnpServiceBinding.InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
            => default;

        void IKnpForwardHost.NotifySessionClosed(int forwardId, IKnpForwardSession session)
        {
            if (_disposed == 0)
            {
                _forwards.TryRemove(new KeyValuePair<int, IKnpForwardSession>(forwardId, session));
            }

            Span<byte> buffer = stackalloc byte[8];
            _bindingId.WriteBigEndian(buffer);
            MemoryMarshal.Write(buffer.Slice(4), ref forwardId);

            _host.TryUnregister(MemoryMarshal.Read<long>(buffer), session);
        }

        ValueTask IKnpForwardHost.ForwardBackAsync(KcpBufferList bufferList, CancellationToken cancellationToken)
            => _host.SendAsync(bufferList, cancellationToken);
    }
}
