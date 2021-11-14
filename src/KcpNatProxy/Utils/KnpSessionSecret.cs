using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KcpNatProxy
{
    internal class KnpSessionSecret
    {
        private Guid _part1;
        private Guid _part2;

        [SkipLocalsInit]
        public KnpSessionSecret()
        {
            Span<byte> buffer = stackalloc byte[32];
            RandomNumberGenerator.Fill(buffer);
            _part1 = MemoryMarshal.Read<Guid>(buffer);
            _part2 = MemoryMarshal.Read<Guid>(buffer.Slice(16));
        }

        public KnpSessionSecret(ReadOnlySpan<byte> secret)
        {
            Debug.Assert(secret.Length == 32);
            _part1 = MemoryMarshal.Read<Guid>(secret);
            _part2 = MemoryMarshal.Read<Guid>(secret.Slice(16));
        }

        public static KnpSessionSecret Replace(KnpSessionSecret? previous, ReadOnlySpan<byte> secret)
        {
            if (previous is null)
            {
                return new KnpSessionSecret(secret);
            }
            if (previous.Equals(secret))
            {
                return previous;
            }
            return new KnpSessionSecret(secret);
        }

        public void Write(Span<byte> destination)
        {
            Debug.Assert(destination.Length == 32);
            MemoryMarshal.Write(destination, ref _part1);
            MemoryMarshal.Write(destination.Slice(16), ref _part2);
        }

        [SkipLocalsInit]
        private bool Equals(ReadOnlySpan<byte> secret)
        {
            Span<byte> buffer = stackalloc byte[32];
            MemoryMarshal.Write(buffer, ref _part1);
            MemoryMarshal.Write(buffer, ref _part2);

            return buffer.SequenceEqual(secret);
        }

        [SkipLocalsInit]
        public void WriteAuthData(ReadOnlySpan<byte> random, Span<byte> destination)
        {
            Debug.Assert(random.Length == 16);
            Debug.Assert(destination.Length >= 32);

            Span<byte> buffer = stackalloc byte[16 + 32];
            random.CopyTo(buffer);
            MemoryMarshal.Write(buffer.Slice(16, 16), ref _part1);
            MemoryMarshal.Write(buffer.Slice(32, 16), ref _part2);

            int bytesWritten = SHA256.HashData(buffer, destination);
            Debug.Assert(bytesWritten == 32);
        }

        [SkipLocalsInit]
        public bool ValidateAuthData(ReadOnlySpan<byte> random, ReadOnlySpan<byte> authResult)
        {
            Span<byte> buffer = stackalloc byte[16 + 32 + 16];

            random.CopyTo(buffer);
            MemoryMarshal.Write(buffer.Slice(16, 16), ref _part1);
            MemoryMarshal.Write(buffer.Slice(32, 16), ref _part2);

            int bytesWritten = SHA256.HashData(buffer, buffer.Slice(48));
            Debug.Assert(bytesWritten == 32);

            return buffer.Slice(48, 16).SequenceEqual(authResult);
        }
    }
}
