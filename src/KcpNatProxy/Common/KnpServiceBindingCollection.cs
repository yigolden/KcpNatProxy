using System;
using System.Collections.Generic;
using System.Threading;

namespace KcpNatProxy
{
    internal sealed class KnpServiceBindingCollection<T> : IKnpServiceBindingHolder, IDisposable where T : notnull, IKnpServiceBinding
    {
        private readonly SimpleLinkedList<T> _bindings = new();
        private readonly ReaderWriterLockSlim _lock = new();
        private bool _dispsed;

        public T? GetAvailableBinding()
        {
            if (_dispsed)
            {
                return default;
            }

            _lock.EnterReadLock();
            try
            {
                if (_dispsed)
                {
                    return default;
                }

                SimpleLinkedListNode<T>? firstNode = _bindings.First;
                if (firstNode is null)
                {
                    return default;
                }
                return firstNode.Value;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public bool Add(T serviceBinding)
        {
            if (_dispsed)
            {
                return false;
            }

            _lock.EnterWriteLock();
            try
            {
                if (_dispsed)
                {
                    return false;
                }

                _bindings.AddLast(new SimpleLinkedListNode<T>(serviceBinding));
                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void Remove(IKnpServiceBinding serviceBinding)
        {
            if (_dispsed)
            {
                return;
            }

            _lock.EnterWriteLock();
            try
            {
                if (_dispsed)
                {
                    return;
                }

                SimpleLinkedListNode<T>? node = _bindings.First;
                while (node is not null)
                {
                    if (ReferenceEquals(node.Value, serviceBinding))
                    {
                        _bindings.Remove(node);
                        return;
                    }
                    node = node.Next;
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void Dispose()
        {
            if (_dispsed)
            {
                return;
            }

            _lock.EnterWriteLock();
            try
            {
                if (_dispsed)
                {
                    return;
                }
                _dispsed = true;

                SimpleLinkedListNode<T>? node = _bindings.First;
                while (node is not null)
                {
                    node.Value.Dispose();
                    node = node.Next;
                }
                _bindings.Clear();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
    }
}
