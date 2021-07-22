using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace KcpNatProxy
{
    internal struct ChapRandom32
    {
        private readonly Guid _id1;
        private readonly Guid _id2;

        public ChapRandom32(ReadOnlySpan<byte> random)
        {
            Debug.Assert(random.Length == 32);

            _id1 = new Guid(random.Slice(0, 16));
            _id2 = new Guid(random.Slice(16, 16));
        }

        [SkipLocalsInit]
        public static ChapRandom32 Create()
        {
            Span<byte> buffer = stackalloc byte[32];
            RandomNumberGenerator.Fill(buffer);
            return new ChapRandom32(buffer);
        }

        public void Write(Span<byte> destination)
        {
            Debug.Assert(destination.Length >= 32);

            _id1.TryWriteBytes(destination.Slice(0, 16));
            _id2.TryWriteBytes(destination.Slice(16, 16));
        }

        [SkipLocalsInit]
        public bool ComputeAndCompare(ReadOnlySpan<byte> password, ReadOnlySpan<byte> hash)
        {
            Span<byte> buffer = stackalloc byte[64 + password.Length];
            Write(buffer);
            password.CopyTo(buffer.Slice(32));
            Span<byte> hashScratchSpace = buffer.Slice(buffer.Length - 32);
            SHA256.TryHashData(buffer.Slice(0, 32 + password.Length), hashScratchSpace, out _);
            return hashScratchSpace.SequenceEqual(hash);
        }

        public void Compute(ReadOnlySpan<byte> password, Span<byte> destination)
        {
            Debug.Assert(destination.Length == 32);

            Span<byte> buffer = stackalloc byte[32 + password.Length];
            Write(buffer);
            password.CopyTo(buffer.Slice(32));
            SHA256.TryHashData(buffer, destination, out _);
        }

    }
}
