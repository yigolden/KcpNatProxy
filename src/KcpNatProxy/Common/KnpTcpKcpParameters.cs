using System;
using System.Buffers.Binary;

namespace KcpNatProxy
{
    internal sealed class KnpTcpKcpParameters
    {
        public ushort WindowSize { get; }
        public ushort QueueSize { get; }
        public ushort UpdateInterval { get; }
        public bool NoDelay { get; }

        public static KnpTcpKcpParameters Default { get; } = new KnpTcpKcpParameters(64, 256, 100, true);

        public KnpTcpKcpParameters(int windowSize, int queueSize, int updateInterval, bool noDelay)
        {
            if (windowSize < 0 || windowSize > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSize));
            }
            if (queueSize < 0 || queueSize > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(queueSize));
            }
            if (updateInterval < 0 || updateInterval > byte.MaxValue * 10)
            {
                throw new ArgumentOutOfRangeException(nameof(updateInterval));
            }
            WindowSize = windowSize == 0 ? (ushort)64 : (ushort)windowSize;
            QueueSize = queueSize == 0 ? (ushort)256 : (ushort)queueSize;
            updateInterval = updateInterval / 10;
            UpdateInterval = updateInterval == 0 ? (ushort)100 : (ushort)(updateInterval * 10);
            NoDelay = noDelay;
        }

        public bool TrySerialize(Span<byte> buffer)
        {
            if (buffer.Length < 4)
            {
                return false;
            }
            BinaryPrimitives.WriteUInt16BigEndian(buffer, WindowSize);
            buffer[2] = (byte)(UpdateInterval / 10);
            buffer[3] = NoDelay ? (byte)1 : (byte)0;
            return true;
        }
    }
}
