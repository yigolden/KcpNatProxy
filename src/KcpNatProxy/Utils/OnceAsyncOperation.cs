using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace KcpNatProxy
{
    internal abstract class OnceAsyncOperation<T> where T : notnull
    {
        private readonly State _state;

        public OnceAsyncOperation(bool manualExecution = false)
        {
            _state = new State(this, manualExecution);
        }

        protected abstract Task<T?> ExecuteAsync(CancellationToken cancellationToken);

        public ValueTask<T?> RunAsync(CancellationToken cancellationToken = default) => _state.RunAsync(cancellationToken);

        protected bool TryInitiate() => _state.TryInitiate();

        protected void Close() => _state.Close();

        protected bool TryReset() => _state.TryReset();

        private class State : IValueTaskSource<T?>, IThreadPoolWorkItem
        {
            private readonly OnceAsyncOperation<T> _parent;
            private readonly bool _manualExecution;
            private int _status; // 0-not started, 1-running, 2-completed, 3-disposed
            private CancellationTokenSource? _cts;

            private ManualResetValueTaskSourceCore<T?> _mrvtsc;
            private bool _activeWait;
            private bool _signaled;
            private CancellationToken _cancellationToken;
            private CancellationTokenRegistration _cancellationRegistration;

            private SimpleLinkedList<WaitItem> _waitList = new();
            private T? _result;

            T? IValueTaskSource<T?>.GetResult(short token)
            {
                _cancellationRegistration.Dispose();

                try
                {
                    return _mrvtsc.GetResult(token);
                }
                finally
                {
                    _mrvtsc.Reset();

                    lock (_waitList)
                    {
                        _activeWait = false;
                        _signaled = false;
                        _cancellationRegistration = default;
                    }
                }
            }
            ValueTaskSourceStatus IValueTaskSource<T?>.GetStatus(short token) => _mrvtsc.GetStatus(token);
            void IValueTaskSource<T?>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _mrvtsc.OnCompleted(continuation, state, token, flags);

            public State(OnceAsyncOperation<T> parent, bool manualExecution)
            {
                _parent = parent;
                _manualExecution = manualExecution;
                _mrvtsc = new ManualResetValueTaskSourceCore<T?> { RunContinuationsAsynchronously = true };
            }

            public ValueTask<T?> RunAsync(CancellationToken cancellationToken)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromCanceled<T?>(cancellationToken);
                }

                WaitItem? waitItem = null;
                short token = 0;
                lock (_waitList)
                {
                    if (_status == 3)
                    {
                        // disposed
                        return default;
                    }
                    if (_status == 2)
                    {
                        // completed
                        return new ValueTask<T?>(_result);
                    }
                    if (_status == 0 && !_manualExecution)
                    {
                        // start task
                        ThreadPool.UnsafeQueueUserWorkItem(this, false);
                    }

                    if (_activeWait)
                    {
                        waitItem = QueueWaitItem(cancellationToken);
                    }
                    else
                    {
                        _activeWait = true;
                        Debug.Assert(!_signaled);
                        _cancellationToken = cancellationToken;
                        token = _mrvtsc.Version;
                    }
                }

                if (waitItem is null)
                {
                    _cancellationRegistration = cancellationToken.UnsafeRegister(state => ((State?)state)!.SetCanceled(), this);
                    return new ValueTask<T?>(this, token);
                }
                else
                {
                    waitItem.RegisterCancellation();
                    return new ValueTask<T?>(waitItem.Task);
                }
            }

            private WaitItem QueueWaitItem(CancellationToken cancellationToken)
            {
                var item = new WaitItem(this, cancellationToken);
                _waitList.AddLast(item.Node);
                return item;
            }

            internal void LockAndRemoveFromQueue(WaitItem item)
            {
                lock (_waitList)
                {
                    _waitList.TryRemove(item.Node);
                }
            }

            private void SetCanceled()
            {
                lock (_waitList)
                {
                    if (_activeWait && !_signaled)
                    {
                        _signaled = true;
                        _mrvtsc.SetException(new OperationCanceledException(_cancellationToken));
                        _cancellationToken = default;
                    }
                }
            }

            private void SetResultAndComplete(T? result)
            {
                lock (_waitList)
                {
                    if (_status != 1)
                    {
                        return;
                    }
                    _status = 1;

                    _result = result;
                    if (_activeWait && !_signaled)
                    {
                        _signaled = true;
                        _mrvtsc.SetResult(result);
                        _cancellationToken = default;
                    }

                    SimpleLinkedListNode<WaitItem>? node = _waitList.First;
                    while (node is not null)
                    {
                        node.Value.NotifyResult(result);
                        node = node.Next;
                    }
                    _waitList.Clear();
                }
            }

            public void Close()
            {
                lock (_waitList)
                {
                    if (_status == 3)
                    {
                        return;
                    }
                    _status = 3;

                    _result = default;
                    if (_activeWait && !_signaled)
                    {
                        _signaled = true;
                        _mrvtsc.SetResult(default);
                        _cancellationToken = default;
                    }

                    SimpleLinkedListNode<WaitItem>? node = _waitList.First;
                    while (node is not null)
                    {
                        node.Value.NotifyResult(default);
                        node = node.Next;
                    }
                    _waitList.Clear();

                    if (_cts is not null)
                    {
                        _cts.Cancel();
                        _cts.Dispose();
                        _cts = null;
                    }
                }
            }

            public bool TryReset()
            {
                lock (_waitList)
                {
                    if (_status != 3)
                    {
                        return false;
                    }
                    _status = 0;
                }
                return true;
            }

            public bool TryInitiate()
            {
                lock (_waitList)
                {
                    if (_status != 0)
                    {
                        return false;
                    }
                    ThreadPool.UnsafeQueueUserWorkItem(this, false);
                }
                return true;

            }

            async void IThreadPoolWorkItem.Execute()
            {
                CancellationTokenSource cts;
                lock (_waitList)
                {
                    if (_status != 0)
                    {
                        return;
                    }
                    _status = 1;
                    cts = _cts = new CancellationTokenSource();
                }

                T? result = default;
                try
                {
                    result = await _parent.ExecuteAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // TODO log this
                    Console.WriteLine(ex);
                }

                SetResultAndComplete(result);
            }

        }

        private class WaitItem : TaskCompletionSource<T?>
        {
            private readonly State _parent;
            private readonly CancellationToken _cancellationToken;
            private CancellationTokenRegistration _cancellationRegistration;
            private bool _signaled;

            public SimpleLinkedListNode<WaitItem> Node { get; }

            public WaitItem(State parent, CancellationToken cancellationToken)
                : base(TaskCreationOptions.RunContinuationsAsynchronously)
            {
                _parent = parent;
                _cancellationToken = cancellationToken;
                Node = new SimpleLinkedListNode<WaitItem>(this);
            }

            public void RegisterCancellation()
            {
                _cancellationRegistration = _cancellationToken.UnsafeRegister(state => ((WaitItem?)state)!.NotifyCanceled(), this);
            }

            private void NotifyCanceled()
            {
                _parent.LockAndRemoveFromQueue(this);

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

            internal void NotifyResult(T? result)
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
