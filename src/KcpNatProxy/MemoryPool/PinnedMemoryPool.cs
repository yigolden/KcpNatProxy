using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using KcpSharp;

namespace KcpNatProxy.MemoryPool
{
    internal sealed class PinnedMemoryPool : IKcpBufferPool
    {
        private readonly ConcurrentQueue<PinnedMemoryPoolBlock> _blocks = new ConcurrentQueue<PinnedMemoryPoolBlock>();
        private readonly ConcurrentQueue<PinnedMemoryPoolBlock> _smallBlocks = new ConcurrentQueue<PinnedMemoryPoolBlock>();
        private readonly ConcurrentQueue<PinnedMemoryPoolBlock> _largeBlocks = new ConcurrentQueue<PinnedMemoryPoolBlock>();
        private readonly int _mtu;

        private int _smallBlockSize;
        private int _maximumBlockSize;

        public PinnedMemoryPool(int mtu)
        {
            _mtu = mtu;
            _smallBlockSize = Math.Min(mtu / 2, 512);
            _maximumBlockSize = Math.Max(2 * mtu, 16 * 1024); // 16K
        }

        public KcpRentedBuffer Rent(KcpBufferPoolRentOptions options)
        {
            if (options.Size > _maximumBlockSize)
            {
                return KcpRentedBuffer.FromSharedArrayPool(options.Size);
            }
            ConcurrentQueue<PinnedMemoryPoolBlock> blockQueue;
            int size;
            if (options.Size > _mtu)
            {
                blockQueue = _largeBlocks;
                size = _maximumBlockSize;
            }
            else if (options.Size <= _smallBlockSize)
            {
                blockQueue = _smallBlocks;
                size = _smallBlockSize;
            }
            else
            {
                blockQueue = _blocks;
                size = _mtu;
            }
            (PinnedMemoryPoolBlock block, Memory<byte> memory) = DequeueOrCreate(blockQueue, size);
            Debug.Assert(memory.Length >= options.Size);
            return KcpRentedBuffer.FromMemoryOwner(block, memory);
        }

        private (PinnedMemoryPoolBlock Block, Memory<byte> Memory) DequeueOrCreate(ConcurrentQueue<PinnedMemoryPoolBlock> queue, int size)
        {
            const int MaxRetryCount = 16;
            int count = 0;
            PinnedMemoryPoolBlock? firstBlock = null;
            while (queue.TryDequeue(out PinnedMemoryPoolBlock? block))
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
                return (new PinnedMemoryPoolBlock(this, buffer), buffer.AsMemory());
            }
            else
            {
                firstBlock.ReplaceArray(buffer);
                return (firstBlock, buffer.AsMemory());
            }
        }

        public void Return(PinnedMemoryPoolBlock block)
        {
            if (block.TryGetMemory(out Memory<byte> memory))
            {
                if (memory.Length > _mtu)
                {
                    _largeBlocks.Enqueue(block);
                    return;
                }
                else if (memory.Length <= _smallBlockSize)
                {
                    _smallBlocks.Enqueue(block);
                    return;
                }
                Debug.Assert(memory.Length == _mtu);
                _blocks.Enqueue(block);
            }
        }
    }
}
