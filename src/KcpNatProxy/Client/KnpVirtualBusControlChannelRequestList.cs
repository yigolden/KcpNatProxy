using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy.Client
{
    internal class KnpVirtualBusControlChannelRequestList : IThreadPoolWorkItem
    {
        private readonly KcpConversation _conversation;
        private readonly ILogger _logger;

        private readonly LinkedList<KnpVirtualBusControlChannelRequestBase> _queries = new();
        private readonly AsyncAutoResetEvent<KnpVirtualBusControlChannelRequestBase?> _querySignal = new();
        private int _state; // 0-not started 1-running 2-disposed;
        private CancellationTokenSource? _cts;

        public KnpVirtualBusControlChannelRequestList(KcpConversation conversation, ILogger logger)
        {
            _conversation = conversation;
            _logger = logger;
        }

        public void Start()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                return;
            }

            CancellationTokenSource? cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _cts, cts);

            ThreadPool.UnsafeQueueUserWorkItem(this, false);

            if (_state == 2)
            {
                cts = Interlocked.Exchange<CancellationTokenSource?>(ref _cts, null);
                if (cts is not null)
                {
                    cts.Cancel();
                    cts.Dispose();
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _state, 2) == 2)
            {
                return;
            }

            CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
            if (cts is not null)
            {
                cts.Cancel();
                cts.Dispose();
            }

            lock (_queries)
            {
                LinkedListNode<KnpVirtualBusControlChannelRequestBase>? node = _queries.First;
                LinkedListNode<KnpVirtualBusControlChannelRequestBase>? next = node?.Next;
                while (node is not null)
                {
                    node.Value.Dispose();
                    _queries.Remove(node);

                    node = next;
                    next = node?.Next;
                }
            }

            _querySignal.TrySet(null);
        }

        public ValueTask<TResult?> SendAsync<TParameter, TResult>(TParameter parameter, CancellationToken cancellationToken) where TParameter : notnull, IKnpVirtualBusControlChannelRequestParameter<TResult> where TResult : notnull
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<TResult?>(cancellationToken);
            }

            KnpVirtualBusControlChannelRequestBase<TResult>? request = null;
            lock (_queries)
            {
                if (_state == 2)
                {
                    return default;
                }
                foreach (KnpVirtualBusControlChannelRequestBase item in _queries)
                {
                    if (item is KnpVirtualBusControlChannelRequestBase<TResult> query)
                    {
                        if (query.CheckParameterMatches(item))
                        {
                            request = query;
                            break;
                        }
                    }
                }
                if (request is null)
                {
                    request = parameter.CreateRequest();
                    _queries.AddLast(request);
                }
            }

            ValueTask<TResult?> task = request.WaitAsync(Environment.TickCount64, cancellationToken);
            if (!task.IsCompleted)
            {
                _querySignal.TrySet(request);
            }
            return task;
        }

        async void IThreadPoolWorkItem.Execute()
        {
            CancellationTokenSource? cts = Volatile.Read(ref _cts);
            if (cts is null || cts.IsCancellationRequested)
            {
                return;
            }
            CancellationToken cancellationToken = cts.Token;

            try
            {
                LinkedListNode<KnpVirtualBusControlChannelRequestBase>? node = _queries.First;
                LinkedListNode<KnpVirtualBusControlChannelRequestBase>? next = node?.Next;
                KnpVirtualBusControlChannelRequestBase? query = node?.Value;

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (query is null)
                    {
                        query = await _querySignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                        if (query is null)
                        {
                            break;
                        }
                    }

                    // send and receive query
                    long tick = Environment.TickCount64;
                    if (!query.IsExpired(tick))
                    {
                        if (!await SendAndReceiveAsync(query, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }
                        if (!query.AllowCache)
                        {
                            lock (_queries)
                            {
                                if (node is not null && node.Previous != null && node.Next != null)
                                {
                                    _queries.Remove(node);
                                }
                            }
                        }
                    }

                    if (node is null)
                    {
                        node = _queries.First;
                        next = node?.Next;
                        query = node?.Value;
                    }
                    else
                    {
                        node = next;
                        next = next?.Next;
                        query = node?.Value;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.LogClientUnhandledException(_logger, ex);
                return;
            }
            finally
            {
                Dispose();
            }
        }

        private async Task<bool> SendAndReceiveAsync(KnpVirtualBusControlChannelRequestBase query, CancellationToken cancellationToken)
        {
            if (!await _conversation.WaitForSendQueueAvailableSpaceAsync(0, 1, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
            if (!SendQueryRequest(_conversation, query))
            {
                return false;
            }
            KcpConversationReceiveResult result = await _conversation.WaitToReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (result.BytesReceived == 0)
            {
                return false;
            }
            if (!ProcessQueryResponse(_conversation, result, query))
            {
                return false;
            }

            return true;
        }

        [SkipLocalsInit]
        private static bool SendQueryRequest(KcpConversation conversation, KnpVirtualBusControlChannelRequestBase query)
        {
            Span<byte> buffer = stackalloc byte[256];
            int bytesWritten = query.WriteRequest(buffer);
            if (bytesWritten == 0)
            {
                return false;
            }
            return conversation.TrySend(buffer.Slice(0, bytesWritten));
        }

        [SkipLocalsInit]
        private static bool ProcessQueryResponse(KcpConversation conversation, KcpConversationReceiveResult receiveResult, KnpVirtualBusControlChannelRequestBase query)
        {
            Span<byte> buffer = stackalloc byte[256];
            if (receiveResult.BytesReceived > 256)
            {
                return false;
            }
            if (!conversation.TryReceive(buffer, out receiveResult))
            {
                return false;
            }
            Debug.Assert(receiveResult.BytesReceived != 0);
            if (buffer[0] == 0xff)
            {
                // fatal error
                return false;
            }
            query.SetResult(buffer.Slice(0, receiveResult.BytesReceived));
            return true;
        }

    }
}
