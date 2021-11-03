using System;
using System.Diagnostics.CodeAnalysis;

namespace KcpNatProxy
{
    public readonly struct KcpRentedBufferList : IDisposable
    {
        private readonly KcpBufferListThreadCache _cache;
        private readonly int _token;

        public static KcpRentedBufferList Allocate(ReadOnlyMemory<byte> buffer)
        {
            return KcpBufferListThreadCache.Allocate(buffer);
        }

        internal KcpRentedBufferList(KcpBufferListThreadCache cache, int token)
        {
            _cache = cache;
            _token = token;
        }

        public KcpBufferList AddPreBuffer(ReadOnlyMemory<byte> preBuffer)
        {
            if (_cache is null)
            {
                ThrowInvalidOperationException();
            }
            return _cache.AddPreBuffer(_token, preBuffer);
        }

        public int GetLength()
        {
            if (_cache is null)
            {
                ThrowInvalidOperationException();
            }
            return _cache.GetLength(_token);
        }

        public void ConsumeAndReturn(Span<byte> destination)
        {
            if (_cache is null)
            {
                ThrowInvalidOperationException();
            }
            _cache.ConsumeAndReturn(_token, destination);
        }

        public static implicit operator KcpBufferList(KcpRentedBufferList bufferList)
        {
            return new KcpBufferList(bufferList._cache, bufferList._token);
        }
        public static KcpBufferList FromKcpRentedBufferList(KcpRentedBufferList bufferList)
        {
            return new KcpBufferList(bufferList._cache, bufferList._token);
        }

        public void Dispose()
        {
            if (_cache is not null)
            {
                _cache.ClearAndReturn(_token);
            }
        }

        [DoesNotReturn]
        private static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException();
        }
    }
}
