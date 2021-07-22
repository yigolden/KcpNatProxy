using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed class KcpNatProxyServerAuthenticationContext
    {
        private readonly KcpConversation _conversation;
        private readonly byte[]? _password;
        private readonly ILogger _logger;
        private int _mtu;

        private KcpNatProxyAuthenticationMethod _chosenMethod;
        private ChapRandom32 _chapRandom;

        public int NegotiatedMtu => _mtu;

        public KcpNatProxyServerAuthenticationContext(KcpConversation conversation, byte[]? password, int mtu, ILogger logger)
        {
            Debug.Assert(mtu >= 256 && mtu <= 60000);
            _conversation = conversation;
            _password = password;
            _mtu = mtu;
            _logger = logger;
        }

        public async Task<bool> AuthenticateAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                cancellationToken = cts.Token;

                // wait for authentication request
                KcpConversationReceiveResult result = await _conversation.WaitToReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (result.TransportClosed)
                {
                    return false;
                }

                if (!ProcessAuthenticationRequest(result))
                {
                    return false;
                }

                // wait for client answer
                result = await _conversation.WaitToReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (result.TransportClosed)
                {
                    return false;
                }

                if (!ProcessClientAnswer(result))
                {
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                await _conversation.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<bool> SendSessionIdAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                cancellationToken = cts.Token;

                if (!SendSessionId(sessionId))
                {
                    return false;
                }

                return await _conversation.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        [SkipLocalsInit]
        private bool ProcessAuthenticationRequest(KcpConversationReceiveResult result)
        {
            if (result.BytesReceived < 8 || result.BytesReceived > 16)
            {
                return false;
            }

            Span<byte> stackBuffer = stackalloc byte[36];
            if (!_conversation.TryReceive(stackBuffer, out result) || result.BytesReceived < 6)
            {
                return false;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(stackBuffer) != 0x7074616e)
            {
                return false;
            }
            if (stackBuffer[4] != 1)
            {
                return false;
            }
            ushort mtu = BinaryPrimitives.ReadUInt16LittleEndian(stackBuffer.Slice(5));
            if ((stackBuffer[7] + 8) > result.BytesReceived)
            {
                return false;
            }
            if (mtu < 256 || mtu > 60000)
            {
                stackBuffer[0] = 1; // status
                BinaryPrimitives.WriteUInt16LittleEndian(stackBuffer.Slice(1), (ushort)_mtu);
                stackBuffer[3] = 0; // method
                _conversation.TrySend(stackBuffer.Slice(0, 4));
                return false;
            }
            _mtu = Math.Min(_mtu, mtu);

            KcpNatProxyAuthenticationMethod? chosenMethod = null;
            Span<byte> buffer = stackBuffer.Slice(8, stackBuffer[7]);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == 0 && _password is null)
                {
                    chosenMethod = KcpNatProxyAuthenticationMethod.None;
                    break;
                }
                if (!chosenMethod.HasValue && buffer[i] == 1 && _password is not null)
                {
                    chosenMethod = KcpNatProxyAuthenticationMethod.PlainText;
                }
                if (buffer[i] == 2 && _password is not null)
                {
                    chosenMethod = KcpNatProxyAuthenticationMethod.Chap;
                    break;
                }
            }

            if (!chosenMethod.HasValue)
            {
                stackBuffer[0] = 1; // status
                BinaryPrimitives.WriteUInt16LittleEndian(stackBuffer.Slice(1), (ushort)_mtu);
                stackBuffer[3] = 0; // method
                _conversation.TrySend(stackBuffer.Slice(0, 4));
                return false;
            }

            _chosenMethod = chosenMethod.GetValueOrDefault();
            stackBuffer[0] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(stackBuffer.Slice(1), (ushort)_mtu);
            stackBuffer[3] = (byte)chosenMethod.GetValueOrDefault();
            if (chosenMethod.GetValueOrDefault() == KcpNatProxyAuthenticationMethod.Chap)
            {
                _chapRandom = ChapRandom32.Create();
                _chapRandom.Write(stackBuffer.Slice(4, 32));
                return _conversation.TrySend(stackBuffer.Slice(0, 36));
            }
            return _conversation.TrySend(stackBuffer.Slice(0, 4));
        }

        [SkipLocalsInit]
        private bool ProcessClientAnswer(KcpConversationReceiveResult result)
        {
            if (result.BytesReceived == 0)
            {
                return false;
            }

            Span<byte> buffer = stackalloc byte[38];
            if (!_conversation.TryReceive(buffer, out result))
            {
                return false;
            }
            if (buffer[0] != 3)
            {
                return false;
            }
            buffer = buffer.Slice(1, result.BytesReceived - 1);

            if (_chosenMethod == KcpNatProxyAuthenticationMethod.None)
            {
                return true;
            }
            else if (_chosenMethod == KcpNatProxyAuthenticationMethod.PlainText)
            {
                if (buffer.IsEmpty)
                {
                    goto SendInvalidAnswer;
                }
                int length = buffer[0];
                if (length > 36 || (buffer.Length - 1) < length)
                {
                    goto SendInvalidAnswer;
                }
                buffer = buffer.Slice(1, length);
                if (buffer.IsEmpty || !buffer.SequenceEqual(_password))
                {
                    goto SendInvalidAnswer;
                }
            }
            else if (_chosenMethod == KcpNatProxyAuthenticationMethod.Chap)
            {
                if (buffer.Length != 32)
                {
                    goto SendInvalidAnswer;
                }

                if (!_chapRandom.ComputeAndCompare(_password, buffer))
                {
                    goto SendInvalidAnswer;
                }
            }
            else
            {
                goto SendInvalidAnswer;
            }

            return true;

        SendInvalidAnswer:
            buffer[0] = 1;
            _conversation.TrySend(buffer.Slice(0, 1));
            return false;
        }

        [SkipLocalsInit]
        private bool SendSessionId(Guid sessionId)
        {
            Span<byte> buffer = stackalloc byte[18];
            buffer[0] = 0;
            sessionId.TryWriteBytes(buffer.Slice(1));
            return _conversation.TrySend(buffer.Slice(0, 17));
        }
    }
}
