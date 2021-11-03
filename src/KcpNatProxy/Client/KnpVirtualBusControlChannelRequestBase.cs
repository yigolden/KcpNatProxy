using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy.Client
{
    internal abstract class KnpVirtualBusControlChannelRequestBase : IDisposable
    {
        public virtual bool AllowCache => false;
        public virtual bool IsExpired(DateTime utcNow) => !AllowCache;

        public abstract int WriteRequest(Span<byte> buffer);
        public abstract void SetResult(ReadOnlySpan<byte> data);
        public abstract void Dispose();
    }

    internal abstract class KnpVirtualBusControlChannelRequestBase<TResult> : KnpVirtualBusControlChannelRequestBase where TResult : notnull
    {
        private readonly SimpleLinkedList<CallbackNode> _waitList = new();
        private TResult? _result;
        private long _lastUpdateTime;
        private bool _updated;
        private bool _disposed;

        public abstract bool CheckParameterMatches<TParameter>(TParameter parameter) where TParameter : notnull;
        public abstract TResult? ParseResponse(ReadOnlySpan<byte> data);
        public DateTime? LastUpdateTime => _updated ? DateTime.FromBinary(Interlocked.Read(ref _lastUpdateTime)) : null;

        private void LockAndRemove(CallbackNode callback)
        {
            lock (_waitList)
            {
                _waitList.TryRemove(callback.Node);
            }
        }

        public ValueTask<TResult?> WaitAsync(DateTime utcNow, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return default;
            }

            CallbackNode callback;
            lock (_waitList)
            {
                if (_disposed)
                {
                    return default;
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromCanceled<TResult?>(cancellationToken);
                }

                if (_updated && !IsExpired(utcNow))
                {
                    return new ValueTask<TResult?>(_result);
                }

                callback = new CallbackNode(this, cancellationToken);
                _waitList.AddLast(callback.Node);
            }

            callback.RegisterCancellation();
            return new ValueTask<TResult?>(callback.Task);
        }

        public override void SetResult(ReadOnlySpan<byte> data)
        {
            lock (_waitList)
            {
                _result = ParseResponse(data);
                Interlocked.Exchange(ref _lastUpdateTime, DateTime.UtcNow.ToBinary());
                _updated = true;

                SimpleLinkedListNode<CallbackNode>? node = _waitList.First;
                while (node is not null)
                {
                    node.Value.NotifyResult(_result);
                    node = node.Next;
                }
                _waitList.Clear();
            }
        }

        public override void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            lock (_waitList)
            {
                _disposed = true;

                SimpleLinkedListNode<CallbackNode>? node = _waitList.First;
                while (node is not null)
                {
                    node.Value.NotifyResult(default);
                    node = node.Next;
                }
                _waitList.Clear();
            }
        }

        class CallbackNode : TaskCompletionSource<TResult?>
        {
            private readonly KnpVirtualBusControlChannelRequestBase<TResult> _parent;
            private readonly CancellationToken _cancellationToken;
            private CancellationTokenRegistration _cancellationRegistration;
            private bool _signaled;

            public SimpleLinkedListNode<CallbackNode> Node { get; }

            public CallbackNode(KnpVirtualBusControlChannelRequestBase<TResult> parent, CancellationToken cancellationToken)
            {
                _parent = parent;
                _cancellationToken = cancellationToken;
                Node = new(this);
            }

            public void RegisterCancellation()
            {
                _cancellationRegistration = _cancellationToken.UnsafeRegister(state => ((CallbackNode?)state)!.NotifyCanceled(), this);
            }

            private void NotifyCanceled()
            {
                _parent.LockAndRemove(this);

                CancellationTokenRegistration cancellationRegistration;
                lock (this)
                {
                    cancellationRegistration = _cancellationRegistration;
                    _cancellationRegistration = default;
                    if (!_signaled)
                    {
                        _signaled = true;
                        TrySetCanceled(_cancellationToken);
                    }
                }

                cancellationRegistration.Dispose();
            }

            internal void NotifyResult(TResult? result)
            {
                CancellationTokenRegistration cancellationRegistration;
                lock (this)
                {
                    cancellationRegistration = _cancellationRegistration;
                    _cancellationRegistration = default;
                    if (!_signaled)
                    {
                        _signaled = true;
                        TrySetResult(result);
                    }
                }

                cancellationRegistration.Dispose();
            }
        }
    }
}
