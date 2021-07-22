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
    internal sealed class KcpNatProxyClientAuthenticationContext
    {
        private readonly KcpConversation _conversation;
        private readonly byte[]? _password;
        private readonly ILogger _logger;
        private int _mtu;

        public int NegotiatedMtu => _mtu;

        public KcpNatProxyClientAuthenticationContext(KcpConversation conversation, byte[]? password, int mtu, ILogger logger)
        {
            Debug.Assert(mtu >= 256 && mtu <= 60000);
            _conversation = conversation;
            _password = password;
            _mtu = mtu;
            _logger = logger;
        }

        public async Task<Guid?> AuthenticateAsync(CancellationToken cancellationToken)
        {
            CancellationToken originalCancellationToken = cancellationToken;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                cancellationToken = cts.Token;

                if (!SendAuthenticationRequest())
                {
                    return null;
                }

                // wait for server response
                KcpConversationReceiveResult result = await _conversation.WaitToReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (result.TransportClosed)
                {
                    return null;
                }

                // process and send
                if (!ProcessAuthenticationResposne(result))
                {
                    return null;
                }

                // wait for response
                result = await _conversation.WaitToReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (result.TransportClosed)
                {
                    return null;
                }

                return ProcessServerAcknowledgment(result);
            }
            catch (OperationCanceledException)
            {
                if (!originalCancellationToken.IsCancellationRequested)
                {
                    Log.LogClientAuthenticationTimeout(_logger);
                }
                return null;
            }
            finally
            {
                await _conversation.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        [SkipLocalsInit]
        private bool SendAuthenticationRequest()
        {
            Span<byte> buffer = stackalloc byte[10];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0x7074616e);
            if (_password is null)
            {
                buffer[4] = 1; // command
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(5), (ushort)_mtu);
                buffer[7] = 1; // method length
                buffer[8] = 0; // none
                buffer = buffer.Slice(0, 9);
            }
            else
            {
                buffer[4] = 1; // command
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(5), (ushort)_mtu);
                buffer[7] = 2; // method length
                buffer[8] = 0; // none
                //buffer[9] = 1; // plain-text
                buffer[9] = 2; // chap
                buffer = buffer.Slice(0, 10);
            }

            return _conversation.TrySend(buffer);
        }

        [SkipLocalsInit]
        private bool ProcessAuthenticationResposne(KcpConversationReceiveResult result)
        {
            if (result.BytesReceived > 64)
            {
                return false;
            }

            Span<byte> buffer = stackalloc byte[64];
            if (!_conversation.TryReceive(buffer, out result))
            {
                return false;
            }
            if (result.BytesReceived < 4)
            {
                return false;
            }

            if (buffer[0] != 0)
            {
                Log.LogClientAuthenticationMethodNotAllowed(_logger);
                return false;
            }
            int mtu = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1));
            if (mtu > _mtu)
            {
                Log.LogClientAuthenticationInvalidMtu(_logger);
                return false;
            }
            _mtu = mtu;
            if (buffer[3] == 0) // none
            {
                buffer[0] = 3;
                if (!_conversation.TrySend(buffer.Slice(0, 1)))
                {
                    return false;
                }
            }
            else if (buffer[3] == 1) //plain-text
            {
                // send client answer
                buffer[0] = 3;
                ReadOnlySpan<byte> plainTextPassword = _password.AsSpan();
                if (plainTextPassword.Length > 36)
                {
                    buffer[0] = 5;
                    _ = _conversation.TrySend(buffer.Slice(0, 1));
                    return false;
                }
                buffer[1] = (byte)plainTextPassword.Length;
                plainTextPassword.CopyTo(buffer.Slice(2));
                return _conversation.TrySend(buffer.Slice(0, 2 + plainTextPassword.Length));
            }
            else if (buffer[3] == 2) // chap
            {
                if (result.BytesReceived != (32 + 4) || _password is null)
                {
                    buffer[0] = 5;
                    _ = _conversation.TrySend(buffer.Slice(0, 1));
                    return false;
                }

                var chapRandom = new ChapRandom32(buffer.Slice(4, 32));
                chapRandom.Compute(_password, buffer.Slice(1, 32));

                buffer[0] = 3;
                return _conversation.TrySend(buffer.Slice(0, 33));
            }
            else
            {
                buffer[0] = 5;
                _ = _conversation.TrySend(buffer.Slice(0, 1));
                Log.LogClientAuthenticationMethodInvalid(_logger);
                return false;
            }

            return true;
        }

        [SkipLocalsInit]
        private Guid? ProcessServerAcknowledgment(KcpConversationReceiveResult result)
        {
            if (result.BytesReceived < 1 || result.BytesReceived > 17)
            {
                return null;
            }

            Span<byte> buffer = stackalloc byte[18];
            if (!_conversation.TryReceive(buffer, out result))
            {
                return null;
            }

            if (buffer[0] != 0)
            {
                Log.LogClientAuthenticationCredentialInvalid(_logger);
                return null;
            }
            if (result.BytesReceived < 17)
            {
                return null;
            }

            return new Guid(buffer.Slice(1, 16));
        }
    }
}
