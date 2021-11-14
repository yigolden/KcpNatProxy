using System;

namespace KcpNatProxy
{
    internal sealed class KcpBufferListThreadCache
    {
        private ReadOnlyMemory<byte>[] _list;
        private int _count;
        private int _token;

        [ThreadStatic]
        private static KcpBufferListThreadCache? s_cache;

        private static KcpBufferListThreadCache GetThreadCache()
        {
            KcpBufferListThreadCache? cache = s_cache;
            if (cache is not null)
            {
                s_cache = null;
                return cache;
            }
            return new KcpBufferListThreadCache();
        }

        internal static KcpRentedBufferList Allocate(ReadOnlyMemory<byte> buffer)
        {
            KcpBufferListThreadCache cache = GetThreadCache();
            if (!buffer.IsEmpty)
            {
                cache._list[0] = buffer;
                cache._count = 1;
            }
            return new KcpRentedBufferList(cache, cache._token);
        }



        private KcpBufferListThreadCache()
        {
            _list = new ReadOnlyMemory<byte>[16];
            _count = 0;
            _token = Random.Shared.Next();
        }

        public KcpBufferList AddPreBuffer(int token, ReadOnlyMemory<byte> preBuffer)
        {
            if (_token != token)
            {
                ThrowInvalidOperationException();
            }
            if ((uint)_count >= (uint)_list.Length)
            {
                ThrowInvalidOperationException();
            }
            _list[_count++] = preBuffer;
            return new KcpBufferList(this, _token);
        }

        public bool CheckIsEmpty(int token)
        {
            if (_token != token)
            {
                ThrowInvalidOperationException();
            }
            return _count == 0;
        }

        public int GetLength(int token)
        {
            if (_token != token)
            {
                ThrowInvalidOperationException();
            }
            int length = 0;
            for (int i = 0; i < _count; i++)
            {
                length += _list[i].Length;
            }
            return length;
        }

        public void ClearAndReturn(int token)
        {
            if (_token != token)
            {
                return;
            }
            Array.Clear(_list);
            _count = 0;
            _token++;
            s_cache = this;
        }

        public void ConsumeAndReturn(int token, Span<byte> destination)
        {
            if (_token != token)
            {
                ThrowInvalidOperationException();
            }

            for (int i = _count - 1; i >= 0; i--)
            {
                ReadOnlySpan<byte> span = _list[i].Span;
                span.CopyTo(destination);
                destination = destination.Slice(span.Length);
            }

            Array.Clear(_list);
            _count = 0;
            _token++;
            s_cache = this;
        }

        private static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException();
        }
    }
}
