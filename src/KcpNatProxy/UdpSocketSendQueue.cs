using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace KcpNatProxy
{
    internal class UdpSocketSendQueue : IDisposable
    {
        private readonly Socket _socket;
        private readonly int _capacity;

        private int _operationCount;
        private int _loopActive;
        private bool _disposed;
        private readonly LinkedList<SendOperation> _queue;
        private readonly LinkedList<SendOperation> _cache;

        private SpinLock _cacheLock;
        private int _nextId;

        public UdpSocketSendQueue(Socket socket, int capacity)
        {
            _socket = socket;
            _capacity = capacity;

            _queue = new LinkedList<SendOperation>();
            _cache = new LinkedList<SendOperation>();
        }

        private int GetNextId()
        {
            while (true)
            {
                int id = Interlocked.Increment(ref _nextId);
                if (id != 0)
                {
                    return id;
                }
            }
        }

        public ValueTask SendAsync(EndPoint endPoint, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _operationCount) > _capacity)
            {
                Interlocked.Decrement(ref _operationCount);
                return default;
            }

            LinkedListNode<SendOperation>? node;
            SendOperation operation;

            bool lockTaken = false;
            try
            {
                _cacheLock.Enter(ref lockTaken);

                node = _cache.First;
                if (node is not null)
                {
                    _cache.Remove(node);
                }
            }
            finally
            {
                if (lockTaken)
                {
                    _cacheLock.Exit();
                }
            }

            if (node is null)
            {
                operation = new SendOperation(this);
                node = operation.Node;
            }
            else
            {
                operation = node.Value;
            }

            // create loop thread or add to the queue
            lock (_queue)
            {
                _queue.AddLast(node);

                if (_loopActive == 0)
                {
                    _loopActive = GetNextId();

                    _ = Task.Run(() => SendLoopAsync(_loopActive), CancellationToken.None);
                }

                return operation.StartAsync(endPoint, packet, cancellationToken);
            }
        }

        private void ReturnOperation(SendOperation operation)
        {
            if (_disposed)
            {
                return;
            }

            bool lockTaken = false;
            try
            {
                _cacheLock.Enter(ref lockTaken);

                _cache.AddLast(operation.Node);
            }
            finally
            {
                if (lockTaken)
                {
                    _cacheLock.Exit();
                }
            }
        }

        private async Task SendLoopAsync(int id)
        {
            Debug.Assert(_loopActive == id);
            try
            {
                while (!_disposed)
                {
                    LinkedListNode<SendOperation>? node;

                    lock (_queue)
                    {
                        node = _queue.First;

                        if (node is null)
                        {
                            _loopActive = 0;
                            return;
                        }
                        else
                        {
                            _queue.Remove(node);
                        }
                    }

                    try
                    {
                        await node.Value.SendAsync(_socket).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _operationCount);
                        node.Value.NotifySendComplete();
                    }
                }
            }
            finally
            {
                lock (_queue)
                {
                    if (_loopActive == id)
                    {
                        _loopActive = 0;
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            lock (_queue)
            {
                foreach (SendOperation operation in _queue)
                {
                    operation.SetDisposed();
                }

                _queue.Clear();
            }

            bool lockTaken = false;
            try
            {
                _cacheLock.Enter(ref lockTaken);

                _cache.Clear();
            }
            finally
            {
                if (lockTaken)
                {
                    _cacheLock.Exit();
                }
            }
        }

        class SendOperation : IValueTaskSource
        {
            private readonly UdpSocketSendQueue _queue;
            private LinkedListNode<SendOperation> _node;
            private ManualResetValueTaskSourceCore<bool> _mrvtsc;
            private SpinLock _lock;

            public LinkedListNode<SendOperation> Node => _node;

            private bool _consumed;
            private EndPoint? _endPoint;
            private ReadOnlyMemory<byte> _packet;
            private CancellationToken _cancellationToken;
            private CancellationTokenRegistration _cancellationRegistration;

            public SendOperation(UdpSocketSendQueue queue)
            {
                _queue = queue;
                _node = new LinkedListNode<SendOperation>(this);
                _mrvtsc = new ManualResetValueTaskSourceCore<bool>
                {
                    RunContinuationsAsynchronously = true
                };
            }

            public ValueTask StartAsync(EndPoint endPoint, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromCanceled(cancellationToken);
                }

                bool lockTaken = false;
                try
                {
                    _lock.Enter(ref lockTaken);

                    Debug.Assert(_endPoint is null);
                    _consumed = false;
                    _endPoint = endPoint;
                    _packet = packet;
                    _cancellationToken = cancellationToken;

                    return new ValueTask(this, _mrvtsc.Version);
                }
                finally
                {
                    if (lockTaken)
                    {
                        _lock.Exit();

                        _cancellationRegistration = cancellationToken.UnsafeRegister(state => ((SendOperation?)state)!.SetCanceled(), this);
                    }
                }
            }

            private void ClearParameters()
            {
                Debug.Assert(_endPoint is not null);
                _endPoint = null;
                _packet = default;
                _cancellationToken = default;
                _cancellationRegistration.Dispose();
                _cancellationRegistration = default;
            }

            private void SetCanceled()
            {
                bool lockTaken = false;
                try
                {
                    _lock.Enter(ref lockTaken);

                    if (_endPoint is not null && !_consumed)
                    {
                        CancellationToken cancellationToken = _cancellationToken;
                        ClearParameters();
                        _mrvtsc.SetException(new OperationCanceledException(cancellationToken));
                    }
                }
                finally
                {
                    if (lockTaken)
                    {
                        _lock.Exit();
                    }
                }
            }

            public void SetDisposed()
            {
                bool lockTaken = false;
                try
                {
                    _lock.Enter(ref lockTaken);

                    if (_endPoint is not null && !_consumed)
                    {
                        _mrvtsc.SetResult(false);
                    }
                }
                finally
                {
                    if (lockTaken)
                    {
                        _lock.Exit();
                    }
                }
            }

            ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _mrvtsc.GetStatus(token);
            void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
                => _mrvtsc.OnCompleted(continuation, state, token, flags);
            void IValueTaskSource.GetResult(short token)
            {
                bool lockTaken = false;
                bool consumed = false;
                try
                {
                    _lock.Enter(ref lockTaken);

                    consumed = _consumed;
                    _mrvtsc.GetStatus(token);
                }
                finally
                {
                    ClearParameters();

                    if (lockTaken)
                    {
                        _lock.Exit();
                    }

                    _mrvtsc.Reset();

                    if (consumed)
                    {
                        _queue.ReturnOperation(this);
                    }
                }
            }

            public ValueTask<int> SendAsync(Socket socket)
            {
                bool lockTaken = false;
                try
                {
                    _lock.Enter(ref lockTaken);

                    if (_endPoint is null)
                    {
                        return default;
                    }

                    return socket.SendToAsync(_packet, SocketFlags.None, _endPoint, _cancellationToken);
                }
                finally
                {
                    if (lockTaken)
                    {
                        _lock.Exit();
                    }
                }
            }

            public void NotifySendComplete()
            {
                bool lockTaken = false;
                try
                {
                    _lock.Enter(ref lockTaken);

                    if (_endPoint is null)
                    {
                        _queue.ReturnOperation(this);
                    }
                    else
                    {
                        _consumed = true;
                        _mrvtsc.SetResult(true);
                    }
                }
                finally
                {
                    if (lockTaken)
                    {
                        _lock.Exit();
                    }
                }
            }
        }
    }
}
