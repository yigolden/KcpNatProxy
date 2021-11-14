using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;

namespace KcpNatProxy.NetworkConnection
{
    internal sealed class KcpNetworkConnectionNegotiationOperation : TaskCompletionSource<bool>, IThreadPoolWorkItem
    {
        private readonly KcpNetworkConnection _networkConnection;
        private readonly IKcpConnectionNegotiationContext _negotiationContext;

        private bool _isCanceled;
        private bool _isDisposed;
        private CancellationToken _cancellationToken;
        private CancellationTokenRegistration _cancellationRegistration;

        private object _activityLock = new object();
        private Timer? _timer;

        private byte _sendSerial;
        private uint _sendTick;
        private KcpRentedBuffer _sendBuffer;
        private byte _sendRetryCount;
        private byte _localReadyState; // 0b00-none 0b01-success 0b10-failed
        private bool _sendSuppressed;

        private byte _receiveSerial;
        private KcpRentedBuffer _receiveBuffer;
        private byte _remoteReadyState; // 0b00-none 0b01-success 0b10-failed
        private bool _receivePending;

        private int _tickOperationActive; // 0-no 1-yes

        public KcpNetworkConnectionNegotiationOperation(KcpNetworkConnection networkConnection, IKcpConnectionNegotiationContext negotiationContext)
            : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
            _networkConnection = networkConnection;
            _negotiationContext = negotiationContext;
        }

        public Task<bool> NegotiateAsync(KcpRentedBuffer cachedPacket, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TrySetCanceled(cancellationToken);
                return Task;
            }

            _cancellationToken = cancellationToken;

            lock (_activityLock)
            {
                _sendTick = (uint)Environment.TickCount;

                _timer = new Timer(static state =>
                {
                    var reference = (WeakReference<KcpNetworkConnectionNegotiationOperation>)state!;
                    if (reference.TryGetTarget(out KcpNetworkConnectionNegotiationOperation? target))
                    {
                        target.Execute();
                    }
                }, new WeakReference<KcpNetworkConnectionNegotiationOperation>(this), TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
            }

            _cancellationRegistration = cancellationToken.UnsafeRegister(s => ((KcpNetworkConnectionNegotiationOperation?)s!).NotifyCanceled(), this);

            if (cachedPacket.IsAllocated)
            {
                InputPacket(cachedPacket.Span);
                cachedPacket.Dispose();
            }

            return Task;
        }

        private void NotifyCanceled()
        {
            lock (_activityLock)
            {
                if (!_isDisposed && !_isCanceled)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
                }
                _isCanceled = true;
            }
        }

        public void NotifyDisposed()
        {
            lock (_activityLock)
            {
                if (!_isDisposed && !_isCanceled)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
                }
                _isDisposed = true;
            }
        }

        private void ClearOperation()
        {
            _isDisposed = true;
            _cancellationToken = default;
            _cancellationRegistration.Dispose();
            _cancellationRegistration = default;

            lock (_activityLock)
            {
                if (_timer is not null)
                {
                    _timer.Dispose();
                    _timer = null;
                }

                _sendBuffer.Dispose();
                _sendBuffer = default;

                _receiveBuffer.Dispose();
                _receiveBuffer = default;
            }
        }

        public void Execute()
        {
            if (Interlocked.Exchange(ref _tickOperationActive, 1) != 0)
            {
                return;
            }
            lock (_activityLock)
            {
                if (_isCanceled)
                {
                    CancellationToken cancellationToken = _cancellationToken;
                    ClearOperation();
                    TrySetCanceled(cancellationToken);
                    return;
                }
                if (_isDisposed)
                {
                    ClearOperation();
                    TrySetResult(false);
                    return;
                }
            }

            bool? result = null;
            Exception? exceptionToThrow = null;
            try
            {
                result = UpdateCore();
            }
            catch (Exception ex)
            {
                exceptionToThrow = ex;
            }
            finally
            {
                if (_isDisposed)
                {
                    ClearOperation();
                    TrySetResult(false);
                    result = null;
                }
                else if (_isCanceled)
                {
                    exceptionToThrow = new OperationCanceledException(_cancellationToken);
                    result = null;
                }

                if (exceptionToThrow is not null)
                {
                    _networkConnection.NotifyNegotiationResult(this, false, null);
                    ClearOperation();
                    TrySetException(exceptionToThrow);
                }
                else if (result.HasValue)
                {
                    _networkConnection?.NotifyNegotiationResult(this, result.GetValueOrDefault(), _negotiationContext.NegotiatedMtu);
                    ClearOperation();
                    TrySetResult(result.GetValueOrDefault());
                }

                Interlocked.Exchange(ref _tickOperationActive, 0);
            }
        }

        private bool? UpdateCore()
        {
            const int HeaderSize = 8;

            KcpNetworkConnection networkConnection = _networkConnection;
            IKcpConnectionNegotiationContext negotiationContext = _negotiationContext;
            IKcpBufferPool bufferPool = networkConnection.GetAllocator();

            lock (_activityLock)
            {
                if (_isCanceled || _isDisposed)
                {
                    return false;
                }

                bool? remoteSucceeded = _remoteReadyState == 0b01 ? true : (_remoteReadyState == 0b10 ? false : null);
                if (remoteSucceeded.HasValue)
                {
                    if (!remoteSucceeded.GetValueOrDefault())
                    {
                        return false;
                    }
                    if (_localReadyState == 0b01)
                    {
                        return true;
                    }
                }

                // receiving side
                if (_receivePending)
                {
                    if (!_receiveBuffer.Span.IsEmpty)
                    {
                        negotiationContext.PutNegotiationData(_receiveBuffer.Span);
                    }
                    _receiveBuffer.Dispose();
                    _receiveBuffer = default;
                    _sendSuppressed = false;
                    _receivePending = false;
                }

                // sending side
                if (!_sendSuppressed && (int)((uint)Environment.TickCount - _sendTick) > 0)
                {
                    bool? continueState = null;
                    KcpConnectionNegotiationResult result = KcpConnectionNegotiationResult.ContinuationRequired;
                    if (!_sendBuffer.IsAllocated)
                    {
                        KcpRentedBuffer rentedBuffer = bufferPool.Rent(new KcpBufferPoolRentOptions(HeaderSize + 256, false));
                        result = negotiationContext.MoveNext(rentedBuffer.Span.Slice(HeaderSize, 256));

                        if (remoteSucceeded.HasValue)
                        {
                            Debug.Assert(remoteSucceeded.HasValue);
                            if (result.IsSucceeded)
                            {
                                continueState = true;
                            }
                        }

                        if (result.IsContinuationRequired && result.BytesWritten == 0)
                        {
                            rentedBuffer.Dispose();
                            _sendSuppressed = true;
                            return null;
                        }

                        rentedBuffer = rentedBuffer.Slice(0, HeaderSize + result.BytesWritten);
                        _sendBuffer = rentedBuffer;
                        _localReadyState = result.IsSucceeded ? (byte)0b01 : (result.IsFailed ? (byte)0b10 : (byte)0);
                        Debug.Assert(_sendRetryCount == 0);
                    }

                    FillSendHeaders(_sendBuffer.Span, _localReadyState, _sendSerial, _receiveSerial);
                    bool sendResult = networkConnection.QueueRawPacket(_sendBuffer.Memory);

                    if (!sendResult)
                    {
                        _sendTick = (uint)Environment.TickCount + 1000;
                    }
                    else
                    {
                        _sendTick = (uint)Environment.TickCount + DetermineSendInterval(ref _sendRetryCount);
                    }

                    return result.IsFailed ? false : continueState;
                }
            }

            return null;
        }

        private static void FillSendHeaders(Span<byte> buffer, byte localReadyState, byte localSerial, byte remoteSerial)
        {
            if (buffer.Length < 8)
            {
                Debug.Fail("Invalid buffer.");
                return;
            }

            Debug.Assert(localReadyState >= 0 && localReadyState <= 3);

            buffer[0] = 1;
            buffer[1] = (byte)(0b10000000 + localReadyState);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2), (ushort)(buffer.Length - 4));
            buffer[4] = localSerial;
            buffer[5] = remoteSerial;
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(6), (ushort)(buffer.Length - 8));
        }

        private static uint DetermineSendInterval(ref byte retryCount)
        {
            if (retryCount <= 1)
            {
                retryCount++;
                return 1000;
            }
            if (retryCount <= 4)
            {
                retryCount++;
                return (1u << (retryCount - 1)) * 1000;
            }
            return 10 * 1000;
        }

        public bool InputPacket(ReadOnlySpan<byte> packet)
        {
            // validate packet
            if (packet.Length < 8)
            {
                return false;
            }
            if (packet[0] != 1)
            {
                return false;
            }
            int remoteReadyState = packet[1];
            if ((remoteReadyState & 0b11111100) != 0b10000000)
            {
                return false;
            }
            remoteReadyState = remoteReadyState & 0b11;

            ushort payloadLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2));
            if ((packet.Length - 4) < payloadLength)
            {
                return false;
            }
            packet = packet.Slice(4);

            byte remoteSerial = packet[0];
            byte localSerial = packet[1];
            if (packet[2] != 0)
            {
                return false;
            }
            int negotiationLength = packet[3];

            packet = packet.Slice(4);
            if (packet.Length < negotiationLength)
            {
                return false;
            }
            packet = packet.Slice(0, negotiationLength);

            IKcpBufferPool bufferPool = _networkConnection.GetAllocator();

            lock (_activityLock)
            {
                if (_isCanceled || _isDisposed)
                {
                    return false;
                }

                if (localSerial == (byte)(1 + _sendSerial))
                {
                    _sendSerial++;
                    _sendTick = (uint)Environment.TickCount;
                    _sendBuffer.Dispose();
                    _sendBuffer = default;
                    _sendRetryCount = 0;
                    _sendSuppressed = false;
                }

                if (!_receivePending && remoteSerial == _receiveSerial)
                {
                    _receiveSerial++;
                    if (_receiveBuffer.Span.Length < negotiationLength)
                    {
                        _receiveBuffer.Dispose();
                        _receiveBuffer = bufferPool.Rent(new KcpBufferPoolRentOptions(negotiationLength, false));
                    }
                    if (!packet.IsEmpty)
                    {
                        packet.CopyTo(_receiveBuffer.Span);
                    }
                    _receiveBuffer = _receiveBuffer.Slice(0, negotiationLength);
                    if (_remoteReadyState == 0)
                    {
                        _remoteReadyState = (byte)remoteReadyState;
                    }
                    _receivePending = true;
                }
            }

            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            return true;
        }

        public bool NotifyRemoteProgressing()
        {
            lock (_activityLock)
            {
                if (_isCanceled || _isDisposed)
                {
                    return false;
                }

                if (_remoteReadyState == 0b01)
                {
                    return true;
                }

                _remoteReadyState = 0b01;
                _receivePending = true;
                _sendSuppressed = false;
                _sendTick = (uint)Environment.TickCount;
            }

            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            return true;
        }
    }
}
