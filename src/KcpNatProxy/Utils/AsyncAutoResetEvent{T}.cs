using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace KcpNatProxy
{
    internal sealed class AsyncAutoResetEvent<T> : IValueTaskSource<T>
    {
        private ManualResetValueTaskSourceCore<T> _mrvts;

        private bool _activeWait;
        private bool _signaled;
        private bool _isAvailable;
        private T _value;
        private CancellationToken _cancellationToken;
        private CancellationTokenRegistration _cancellationRegistration;

        ValueTaskSourceStatus IValueTaskSource<T>.GetStatus(short token) => _mrvts.GetStatus(token);
        void IValueTaskSource<T>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _mrvts.OnCompleted(continuation, state, token, flags);
        T IValueTaskSource<T>.GetResult(short token)
        {
            _cancellationRegistration.Dispose();

            try
            {
                return _mrvts.GetResult(token);
            }
            finally
            {
                _mrvts.Reset();
                lock (this)
                {
                    _activeWait = false;
                    _signaled = false;
                    _cancellationRegistration = default;
                }
            }
        }

        public AsyncAutoResetEvent()
        {
            _mrvts = new ManualResetValueTaskSourceCore<T>
            {
                RunContinuationsAsynchronously = true
            };
            _value = default!;
        }

        public ValueTask<T> WaitAsync(CancellationToken cancellationToken)
        {
            short token;
            lock (this)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromCanceled<T>(cancellationToken);
                }
                if (_activeWait)
                {
                    return ValueTask.FromException<T>(new InvalidOperationException());
                }
                if (_isAvailable)
                {
                    T value = _value;
                    _isAvailable = false;
                    _value = default!;
                    return new ValueTask<T>(value);
                }

                _activeWait = true;
                Debug.Assert(!_signaled);
                _cancellationToken = cancellationToken;
                token = _mrvts.Version;
            }

            _cancellationRegistration = cancellationToken.UnsafeRegister(state => ((AsyncAutoResetEvent<T>?)state)!.SetCanceled(), this);
            return new ValueTask<T>(this, token);
        }

        public bool TryGet([MaybeNullWhen(false)] out T value)
        {
            lock (this)
            {
                if (_activeWait)
                {
                    throw new InvalidOperationException();
                }
                if (_isAvailable)
                {
                    value = _value;
                    _value = default!;
                    _isAvailable = false;
                    return true;
                }

                value = default;
                return false;
            }

        }

        private void SetCanceled()
        {
            lock (this)
            {
                if (_activeWait && !_signaled)
                {
                    CancellationToken cancellationToken = _cancellationToken;
                    ClearPreviousOperation();
                    _mrvts.SetException(new OperationCanceledException(cancellationToken));
                }
            }
        }

        private void ClearPreviousOperation()
        {
            _signaled = true;
            _cancellationToken = default;
        }

        public bool TrySet(T value)
        {
            lock (this)
            {
                if (_isAvailable)
                {
                    return false;
                }

                if (_activeWait && !_signaled)
                {
                    ClearPreviousOperation();
                    _mrvts.SetResult(value);
                }
                else
                {
                    _isAvailable = true;
                    _value = value;
                }
                return true;
            }

        }
    }
}
