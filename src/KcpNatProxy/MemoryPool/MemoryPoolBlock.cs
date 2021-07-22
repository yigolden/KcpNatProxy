using System;

namespace KcpNatProxy
{
    /// <summary>
    /// Wraps an array in a reusable block of managed memory
    /// </summary>
    internal sealed class MemoryPoolBlock : IDisposable
    {
        private readonly MemoryPool _pool;
        private readonly WeakReference<byte[]> _arrayRef;

        internal MemoryPoolBlock(MemoryPool pool, byte[] buffer)
        {
            _pool = pool;
            _arrayRef = new WeakReference<byte[]>(buffer);
        }

        public bool TryGetMemory(out Memory<byte> memory)
        {
            if (_arrayRef.TryGetTarget(out byte[]? array))
            {
                memory = array;
                return true;
            }

            memory = default;
            return false;
        }

        public void ReplaceArray(byte[] buffer)
        {
            _arrayRef.SetTarget(buffer);
        }

        public void Dispose()
        {
            _pool.Return(this);
        }
    }
}
