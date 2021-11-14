using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KcpNatProxy
{
    internal class KnpInt32IdAllocator
    {
        private readonly ConcurrentDictionary<uint, bool> _ids = new();

        public KnpRentedInt32 Allocate()
        {
            return new KnpRentedInt32(this, AllocateCore());
        }

        private int AllocateCore()
        {
            uint id = (uint)Environment.TickCount & 0x7fffffff;
            if (id != 0 && _ids.TryAdd(id, true))
            {
                return (int)id;
            }
            return AllocateCoreSlow(id);
        }

        private int AllocateCoreSlow(uint id)
        {
            id = (id + 1) & 0x7fffffff;
            while (true)
            {
                if (id != 0 && _ids.TryAdd(id, true))
                {
                    return (int)id;
                }
                id = (id + 1) & 0x7fffffff;
            }
        }

        public bool Return(int id)
        {
            return _ids.TryRemove((uint)id, out _);
        }
    }

    internal readonly struct KnpRentedInt32 : IDisposable
    {
        private readonly KnpInt32IdAllocator? _allocator;
        private readonly int _id;

        public KnpRentedInt32(int id)
        {
            _allocator = null;
            _id = id;
        }

        public KnpRentedInt32(KnpInt32IdAllocator allocator, int id)
        {
            _allocator = allocator;
            _id = id;
        }

        public void Dispose()
        {
            if (_allocator is null)
            {
                return;
            }
            _allocator.Return(_id);
        }

        public int Value => _id;

        public void Write(Span<byte> buffer) => MemoryMarshal.Write<int>(buffer, ref Unsafe.AsRef(in _id));
        public void WriteBigEndian(Span<byte> buffer) => BinaryPrimitives.WriteInt32BigEndian(buffer, _id);
    }
}
