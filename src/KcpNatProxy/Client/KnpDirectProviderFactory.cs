using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy.Client
{
    internal sealed class KnpDirectProviderFactory : KnpProviderFactory, IKnpVirtualBusProviderFactory
    {
        private readonly string _name;
        private readonly KnpServiceType _serviceType;
        private readonly EndPoint _forwardEndPoint;
        private KnpTcpKcpParameters? _parameters;
        private readonly ILogger _logger;

        public string Name => _name;
        public KnpServiceType ServiceType => _serviceType;

        public KnpDirectProviderFactory(KnpProviderDescription description, ILogger logger)
        {
            if (!description.Validate(out string? errorMessage, out EndPoint? forwardEndPoint))
            {
                throw new ArgumentException(errorMessage, nameof(description));
            }

            Debug.Assert(description.ServiceType == KnpServiceType.Tcp || description.ServiceType == KnpServiceType.Udp);
            _name = description.Name;
            _serviceType = description.ServiceType;
            _forwardEndPoint = forwardEndPoint;
            if (description.ServiceType == KnpServiceType.Tcp)
            {
                _parameters = new KnpTcpKcpParameters(description.WindowSize, description.QueueSize, description.UpdateInterval, description.NoDelay);
            }

            _logger = logger;
        }

        public IKnpProvider CreateProvider(IKnpConnectionHost host, int bindingId, ReadOnlySpan<byte> remoteParameters)
        {
            if (_serviceType == KnpServiceType.Tcp)
            {
                _ = KnpTcpKcpRemoteParameters.TryParse(remoteParameters, out KnpTcpKcpRemoteParameters parameters);
                return new KnpTcpProvider(host, bindingId, _forwardEndPoint, _parameters ?? KnpTcpKcpParameters.Default, parameters);
            }
            else if (_serviceType == KnpServiceType.Udp)
            {
                return new KnpUdpProvider(host, bindingId, _forwardEndPoint);
            }
            throw new NotSupportedException();
        }

        int IKnpVirtualBusProviderFactory.SerializeParameters(Span<byte> buffer)
        {
            var parameters = _parameters;
            if (parameters is null)
            {
                return 0;
            }
            if (parameters.TrySerialize(buffer))
            {
                return 4;
            }
            return 0;
        }

        public override async Task<bool> RegisterAsync(KcpConversation channel, IKnpConnectionHost host, ConcurrentDictionary<int, IKnpProvider> providers, CancellationToken cancellationToken)
        {
            await channel.WaitForSendQueueAvailableSpaceAsync(0, 1, cancellationToken).ConfigureAwait(false);
            SendRequest(channel);
            KcpConversationReceiveResult result = await channel.WaitToReceiveAsync(cancellationToken).ConfigureAwait(false);
            return ReceiveResponse(channel, result, host, providers);
        }

        [SkipLocalsInit]
        private void SendRequest(KcpConversation channel)
        {
            Span<byte> buffer = stackalloc byte[256];
            buffer[0] = 1; // type: 1-bind provider
            buffer[1] = (byte)_serviceType; // service type
            buffer[2] = (byte)Encoding.UTF8.GetByteCount(_name);
            int bytesWritten = 3 + Encoding.UTF8.GetBytes(_name, buffer.Slice(3));
            if (_parameters is not null)
            {
                _parameters.TrySerialize(buffer.Slice(bytesWritten));
                bytesWritten += 4;
                channel.TrySend(buffer.Slice(0, bytesWritten));
            }
            else
            {
                channel.TrySend(buffer.Slice(0, bytesWritten));
            }
        }

        [SkipLocalsInit]
        private bool ReceiveResponse(KcpConversation channel, KcpConversationReceiveResult result, IKnpConnectionHost host, ConcurrentDictionary<int, IKnpProvider> providers)
        {
            Span<byte> buffer = stackalloc byte[12];
            bool succeeded = channel.TryReceive(buffer, out result);
            if (!succeeded)
            {
                return false;
            }

            if (result.BytesReceived == 2)
            {
                if (buffer[0] == 1)
                {
                    int reason = buffer[1];
                    if (reason == 1)
                    {
                        Log.LogClientBindProviderUnsupportedCommand(_logger, _name);
                        return true;
                    }
                    if (reason == 2)
                    {
                        Log.LogClientBindProviderUnsupportedServiceType(_logger, _name);
                        return true;
                    }
                    if (reason == 3)
                    {
                        Log.LogClientBindProviderServiceNotFound(_logger, _name);
                        return true;
                    }
                }
            }

            if (result.BytesReceived < 6)
            {
                Log.LogClientBindProviderInvalidResponse(_logger, _name);
                return false;
            }

            if (!TryParseBindProviderResponse(host, buffer, out int bindingId, out IKnpProvider? provider))
            {
                Log.LogClientBindProviderInvalidResponse(_logger, _name);
                return false;
            }

            if (!providers.TryAdd(bindingId, provider))
            {
                Log.LogClientBindProviderInvalidResponse(_logger, _name);
                provider.Dispose();
                return false;
            }

            Log.LogClientBindProviderSucceeded(_logger, _name);
            return true;
        }

        public bool TryParseBindProviderResponse(IKnpConnectionHost host, ReadOnlySpan<byte> buffer, out int bindingId, [NotNullWhen(true)] out IKnpProvider? provider)
        {
            Debug.Assert(buffer.Length >= 6);
            if (buffer[0] != 0)
            {
                bindingId = 0;
                provider = null;
                return false;
            }
            bindingId = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(2));
            if (bindingId < 0)
            {
                provider = null;
                return false;
            }

            if (_serviceType == KnpServiceType.Tcp)
            {
                _ = KnpTcpKcpRemoteParameters.TryParse(buffer.Slice(6), out KnpTcpKcpRemoteParameters remoteParameters);
                provider = new KnpTcpProvider(host, bindingId, _forwardEndPoint, _parameters ?? KnpTcpKcpParameters.Default, remoteParameters);
                return true;
            }
            else if (_serviceType == KnpServiceType.Udp)
            {
                provider = new KnpUdpProvider(host, bindingId, _forwardEndPoint);
                return true;
            }

            provider = null;
            return false;
        }
    }
}
