using KcpSharp;

namespace KcpNatProxy
{
    internal sealed class DefaultBufferPool : IKcpBufferPool
    {
        public static DefaultBufferPool Instance { get; } = new DefaultBufferPool();

        public KcpRentedBuffer Rent(KcpBufferPoolRentOptions options)
            => KcpRentedBuffer.FromSharedArrayPool(options.Size);
    }
}
