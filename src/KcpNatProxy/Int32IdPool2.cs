using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace KcpNatProxy
{
    internal sealed class Int32IdPool2
    {
        private readonly ConcurrentDictionary<int, bool> _activeIds = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Rent()
        {
            int id = Environment.TickCount;
            if (_activeIds.TryAdd(id, true))
            {
                return id;
            }
            return RentSlow();
        }

        private int RentSlow()
        {
            int id = Environment.TickCount;
            while (true)
            {
                if (_activeIds.TryAdd(id, true))
                {
                    return id;
                }
                id++;
            }
        }

        public void Return(int id)
        {
            _activeIds.TryRemove(id, out _);
        }

        public void Clear()
        {
            _activeIds.Clear();
        }
    }
}
