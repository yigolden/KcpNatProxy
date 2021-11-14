using System;

namespace KcpNatProxy
{
    internal static class ConstantByteArrayCache
    {
        private static readonly byte[] _zeroBytes4 = new byte[4];
        private static readonly byte[] _ffByte = new byte[1] { 0xff };

        internal static ReadOnlyMemory<byte> ZeroBytes4 => new ReadOnlyMemory<byte>(_zeroBytes4, 0, 4);

        internal static ReadOnlyMemory<byte> FFByte => new ReadOnlyMemory<byte>(_ffByte, 0, 1);
    }
}
