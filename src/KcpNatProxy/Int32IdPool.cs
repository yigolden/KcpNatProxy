using System;
using System.Collections.Concurrent;

namespace KcpNatProxy
{
    internal sealed class Int32IdPool
    {
        private const int _maxValue = int.MaxValue - 1;

        private ConcurrentQueue<int> _queue = new();
        private int _nextValue;

        public int Rent()
        {
            if (_queue.TryDequeue(out int id))
            {
                return id;
            }
            lock (_queue)
            {
                id = _nextValue + 1;
                if (id > _maxValue)
                {
                    ThrowInvalidOperationException();
                }
                _nextValue = id;
                return id;
            }
        }

        public void Return(int id)
        {
            _queue.Enqueue(id);
        }

        public void Clear()
        {
            _queue.Clear();
        }

        private static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException();
        }

    }
}
