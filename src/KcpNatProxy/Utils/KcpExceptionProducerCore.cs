using System;
using System.Threading;

namespace KcpNatProxy
{
    internal struct KcpExceptionProducerCore<T>
    {
        private Func<Exception, T, object?, bool>? _exceptionHandler;
        private object? _exceptionState;
        private SpinLock _lock;

        public void SetExceptionHandler(Func<Exception, T, object?, bool> handler, object? state)
        {
            bool lockTaken = false;
            try
            {
                _lock.Enter(ref lockTaken);

                _exceptionHandler = handler;
                _exceptionState = state;
            }
            finally
            {
                if (lockTaken)
                {
                    _lock.Exit();
                }
            }
        }

        public void Clear()
        {
            bool lockTaken = false;
            try
            {
                _lock.Enter(ref lockTaken);

                _exceptionHandler = null;
                _exceptionState = null;
            }
            finally
            {
                if (lockTaken)
                {
                    _lock.Exit();
                }
            }
        }

        public bool RaiseException(T source, Exception ex)
        {
            Func<Exception, T, object?, bool>? handler;
            object? state;

            bool lockTaken = false;
            try
            {
                _lock.Enter(ref lockTaken);

                handler = _exceptionHandler;
                state = _exceptionState;
            }
            finally
            {
                if (lockTaken)
                {
                    _lock.Exit();
                }
            }

            if (handler is not null)
            {
                return handler.Invoke(ex, source, state);
            }
            return false;
        }
    }
}
