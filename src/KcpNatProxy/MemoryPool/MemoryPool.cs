using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using KcpSharp;

namespace KcpNatProxy
{
    internal sealed class MemoryPool : IKcpBufferPool
    {
        private readonly ConcurrentQueue<MemoryPoolBlock> _blocks = new ConcurrentQueue<MemoryPoolBlock>();
        private readonly ConcurrentQueue<MemoryPoolBlock> _largeBlocks = new ConcurrentQueue<MemoryPoolBlock>();
        private readonly int _mtu;

        private const int MaximumBlockSize = ushort.MaxValue;

        public MemoryPool(int mtu)
        {
            _mtu = mtu;
        }

        public KcpRentedBuffer Rent(KcpBufferPoolRentOptions options)
        {
            if (options.Size > MaximumBlockSize)
            {
                return KcpRentedBuffer.FromSharedArrayPool(options.Size);
            }
            if (options.IsOutbound)
            {
                if (options.Size > _mtu)
                {
                    (MemoryPoolBlock block, Memory<byte> memory) = DequeueOrCreate(_largeBlocks, MaximumBlockSize);
                    return KcpRentedBuffer.FromMemoryOwner(block, memory);
                }
                else
                {
                    (MemoryPoolBlock block, Memory<byte> memory) = DequeueOrCreate(_blocks, _mtu);
                    return KcpRentedBuffer.FromMemoryOwner(block, memory);
                }
            }
            return KcpRentedBuffer.FromSharedArrayPool(options.Size);
        }

        private (MemoryPoolBlock Block, Memory<byte> Memory) DequeueOrCreate(ConcurrentQueue<MemoryPoolBlock> queue, int size)
        {
            const int MaxRetryCount = 16;
            int count = 0;
            MemoryPoolBlock? firstBlock = null;
            while (queue.TryDequeue(out MemoryPoolBlock? block))
            {
                if (block.TryGetMemory(out Memory<byte> memory))
                {
                    return (block, memory);
                }
                firstBlock ??= block;

                count++;
                if (count > MaxRetryCount)
                {
                    break;
                }
            }

            byte[] buffer = GC.AllocateUninitializedArray<byte>(size, pinned: true);
            if (firstBlock is null)
            {
                return (new MemoryPoolBlock(this, buffer), buffer.AsMemory());
            }
            else
            {
                firstBlock.ReplaceArray(buffer);
                return (firstBlock, buffer.AsMemory());
            }
        }

        public void Return(MemoryPoolBlock block)
        {
            if (block.TryGetMemory(out Memory<byte> memory))
            {
                if (memory.Length > _mtu)
                {
                    _largeBlocks.Enqueue(block);
                    return;
                }
                Debug.Assert(memory.Length == _mtu);
                _blocks.Enqueue(block);
            }
        }
    }
}
