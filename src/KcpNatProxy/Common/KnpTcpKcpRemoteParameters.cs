using System;
using System.Buffers.Binary;

namespace KcpNatProxy
{
    internal readonly struct KnpTcpKcpRemoteParameters
    {
        private readonly ushort _windowSize;
        private readonly byte _updateIntervalScale;
        private readonly bool _noDelay;

        public ushort WindowSize => _windowSize;
        public ushort UpdateInterval => _updateIntervalScale == 0 ? (ushort)100 : (ushort)(_updateIntervalScale * 10);
        public bool NoDelay => _noDelay;

        public KnpTcpKcpRemoteParameters(int windowSize, int updateInterval, bool noDelay)
        {
            if (windowSize < 0 || windowSize > ushort.MaxValue)
            {
                ThrowArgumentOutOfRangeException(nameof(windowSize));
            }
            if (updateInterval < 0 || updateInterval > byte.MaxValue * 10)
            {
                ThrowArgumentOutOfRangeException(nameof(updateInterval));
            }
            _windowSize = (ushort)windowSize;
            _updateIntervalScale = (byte)(updateInterval / 10);
            _noDelay = noDelay;
        }

        private static void ThrowArgumentOutOfRangeException(string paramName)
        {
            throw new ArgumentOutOfRangeException(paramName);
        }

        public static bool TryParse(ReadOnlySpan<byte> buffer, out KnpTcpKcpRemoteParameters parameters)
        {
            if (buffer.Length < 4)
            {
                parameters = default;
                return false;
            }
            if (buffer[2] == 0)
            {
                parameters = default;
                return false;
            }
            parameters = new KnpTcpKcpRemoteParameters(BinaryPrimitives.ReadUInt16BigEndian(buffer), buffer[2] * 10, (buffer[3] & 1) != 0);
            return true;
        }
    }
}
