using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KcpNatProxy
{
    internal readonly struct KnpChapChallenge
    {
        private readonly long _v1;
        private readonly long _v2;

        public KnpChapChallenge(ReadOnlySpan<byte> challenge)
        {
            Debug.Assert(challenge.Length == 16);
            _v1 = MemoryMarshal.Read<long>(challenge);
            _v2 = MemoryMarshal.Read<long>(challenge.Slice(8));
        }

        [SkipLocalsInit]
        public static KnpChapChallenge Create()
        {
            Span<byte> challenge = stackalloc byte[16];
            RandomNumberGenerator.Fill(challenge);
            return new KnpChapChallenge(challenge);
        }

        public void Write(Span<byte> destination)
        {
            Debug.Assert(destination.Length >= 16);
            MemoryMarshal.Write(destination, ref Unsafe.AsRef(in _v1));
            MemoryMarshal.Write(destination.Slice(8), ref Unsafe.AsRef(in _v2));
        }

        [SkipLocalsInit]
        public void Hash(ReadOnlySpan<byte> password, Span<byte> random, Span<byte> result)
        {
            Debug.Assert(random.Length == 16);
            Debug.Assert(result.Length == 32);

            Span<byte> buffer = stackalloc byte[32 + password.Length];
            RandomNumberGenerator.Fill(random);
            MemoryMarshal.Write(buffer, ref Unsafe.AsRef(in _v1));
            MemoryMarshal.Write(buffer.Slice(8), ref Unsafe.AsRef(in _v2));
            random.CopyTo(buffer.Slice(16));
            password.CopyTo(buffer.Slice(32));

            SHA256.HashData(buffer, result);
        }

        public bool Validate(ReadOnlySpan<byte> random, ReadOnlySpan<byte> password, ReadOnlySpan<byte> authData)
        {
            Debug.Assert(random.Length == 16);

            Span<byte> buffer = stackalloc byte[64 + password.Length];
            MemoryMarshal.Write(buffer.Slice(32), ref Unsafe.AsRef(in _v1));
            MemoryMarshal.Write(buffer.Slice(40), ref Unsafe.AsRef(in _v2));
            random.CopyTo(buffer.Slice(48));
            password.CopyTo(buffer.Slice(64));

            SHA256.HashData(buffer.Slice(32), buffer);
            return buffer.Slice(0, 32).SequenceEqual(authData);

        }
    }
}
