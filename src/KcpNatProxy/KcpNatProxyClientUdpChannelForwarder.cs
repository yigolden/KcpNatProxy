using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed class KcpNatProxyClientUdpChannelForwarder : IDisposable
    {
        private readonly KcpNatProxyClientUdpWorker _clientWorker;
        private readonly EndPoint _forwardEndPoint;
        private readonly IPEndPoint _sourceEndPoint;
        private readonly int _mtu;
        private readonly MemoryPool _memoryPool;
        private readonly ILogger _logger;
        private Channel<(KcpRentedBuffer, int length)>? _packetsPipeline;
        private CancellationTokenSource? _pipeCts;
        private CancellationTokenSource? _receiveCts;
        private Socket? _socket;
        private bool _disposed;
        private DateTime _lastActiveDateTime;

        public KcpNatProxyClientUdpChannelForwarder(KcpNatProxyClientUdpWorker clientWorker, EndPoint forwardEndPoint, IPEndPoint sourceEndPoint, int mtu, MemoryPool memoryPool, ILogger logger)
        {
            _clientWorker = clientWorker;
            _forwardEndPoint = forwardEndPoint;
            _sourceEndPoint = sourceEndPoint;
            _mtu = mtu;
            _memoryPool = memoryPool;
            _logger = logger;
            _lastActiveDateTime = DateTime.UtcNow;
            Debug.Assert(sourceEndPoint.AddressFamily == AddressFamily.InterNetworkV6);
        }

        public bool IsExpired => DateTime.UtcNow.AddSeconds(-90) > _lastActiveDateTime;

        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return default;
            }

            Channel<(KcpRentedBuffer, int length)>? pipeline = _packetsPipeline;
            if (pipeline is null)
            {
                // setup
                pipeline = _packetsPipeline = Channel.CreateBounded<(KcpRentedBuffer, int length)>(new BoundedChannelOptions(16)
                {
                    FullMode = BoundedChannelFullMode.DropWrite,
                    SingleReader = true,
                    SingleWriter = true
                });
                _pipeCts = new CancellationTokenSource();
                _ = Task.Run(() => PumpFromAsync(_pipeCts), CancellationToken.None);
            }

            KcpRentedBuffer owner = _memoryPool.Rent(new KcpBufferPoolRentOptions(packet.Length, true));
            packet.CopyTo(owner.Memory);
            return pipeline.Writer.WriteAsync((owner, packet.Length), cancellationToken);
        }

        private async Task PumpFromAsync(CancellationTokenSource cts)
        {
            Debug.Assert(_packetsPipeline is not null);
            CancellationToken cancellationToken = cts.Token;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    (KcpRentedBuffer owner, int length) = await _packetsPipeline.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    Socket? unconstructedSocket = null;
                    try
                    {
                        if (_socket is null)
                        {
                            unconstructedSocket = new Socket(SocketType.Dgram, ProtocolType.Udp);
                            SocketHelper.PatchSocket(unconstructedSocket);
                            await unconstructedSocket.ConnectAsync(_forwardEndPoint, cancellationToken).ConfigureAwait(false);
                            _socket = Interlocked.Exchange<Socket?>(ref unconstructedSocket, null);
                            _receiveCts = new CancellationTokenSource();
                            _ = Task.Run(() => ReceiveLoop(_receiveCts), CancellationToken.None);
                        }

                        await _socket.SendToAsync(owner.Memory.Slice(0, length), SocketFlags.None, _forwardEndPoint, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not ObjectDisposedException)
                    {
                        Log.LogClientUnhandledExceptionInUdpService(_logger, ex);
                    }
                    finally
                    {
                        owner.Dispose();
#pragma warning disable CA1508 // Avoid dead conditional code
                        unconstructedSocket?.Dispose();
#pragma warning restore CA1508 // Avoid dead conditional code
                    }
                }

            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            finally
            {
                while (_packetsPipeline.Reader.TryRead(out (KcpRentedBuffer, int length) item))
                {
                    item.Item1.Dispose();
                }
                cts.Dispose();
            }
        }

        private async Task ReceiveLoop(CancellationTokenSource cts)
        {
            Debug.Assert(_socket is not null);
            CancellationToken cancellationToken = cts.Token;
            try
            {
                using KcpRentedBuffer memoryHandle = _memoryPool.Rent(new KcpBufferPoolRentOptions(_mtu, true));
                Memory<byte> buffer = memoryHandle.Memory.Slice(0, _mtu);
                Debug.Assert(_sourceEndPoint.AddressFamily == AddressFamily.InterNetworkV6);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Span, _clientWorker.Id);
                _sourceEndPoint.Address.TryWriteBytes(buffer.Span.Slice(4, 16), out _);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.Span.Slice(20, 2), (ushort)_sourceEndPoint.Port);
                while (!cancellationToken.IsCancellationRequested)
                {
                    SocketReceiveFromResult result = await _socket.ReceiveFromAsync(buffer.Slice(24), SocketFlags.None, _forwardEndPoint, cancellationToken).ConfigureAwait(false);

                    _lastActiveDateTime = DateTime.UtcNow;
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Span.Slice(22, 2), (ushort)result.ReceivedBytes);
                    await _clientWorker.SendAsync(buffer.Slice(0, 24 + result.ReceivedBytes), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            finally
            {
                cts.Dispose();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                Interlocked.Exchange(ref _pipeCts, null)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
            try
            {
                Interlocked.Exchange(ref _receiveCts, null)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
            _socket?.Dispose();
        }
    }
}
