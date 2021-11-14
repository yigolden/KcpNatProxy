using System;
using System.Diagnostics.CodeAnalysis;

namespace KcpNatProxy
{
    public readonly ref struct KcpBufferList
    {
        private readonly KcpBufferListThreadCache _cache;
        private readonly int _token;

        internal KcpBufferList(KcpBufferListThreadCache cache, int token)
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

        public bool IsEmpty
        {
            get
            {
                if (_cache is null)
                {
                    ThrowInvalidOperationException();
                }
                return _cache.CheckIsEmpty(_token);
            }
        }

        [DoesNotReturn]
        private static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException();
        }
    }
}
