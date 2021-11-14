using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy.Client
{
    internal sealed class KnpPeerConnectionCollection : IThreadPoolWorkItem
    {
        private readonly KnpPeerConnectionReceiver _peerConnectionReceiver;
        private readonly ConcurrentDictionary<int, KnpPeerConnection> _connections = new();
        private readonly object _stateChangeLock = new();
        private readonly int _maximumMtu;
        private CancellationTokenSource? _cts;

        public KnpPeerConnectionReceiver PeerConnectionReceiver => _peerConnectionReceiver;

        public IKcpBufferPool BufferPool { get; }
        public ILogger Logger { get; }
        public int MaximumMtu => _maximumMtu;

        public KnpPeerConnectionCollection(KnpPeerConnectionReceiver peerConnectionReceiver, int maximumMtu, IKcpBufferPool bufferPool, ILogger logger)
        {
            _peerConnectionReceiver = peerConnectionReceiver;
            _maximumMtu = maximumMtu;
            BufferPool = bufferPool;
            Logger = logger;
        }

        public void Start()
        {
            lock (_stateChangeLock)
            {
                if (_cts is not null)
                {
                    return;
                }
                _cts = new CancellationTokenSource();
            }
            ThreadPool.UnsafeQueueUserWorkItem(this, false);
        }

        public ValueTask DisposeAsync()
        {
            if (_cts is null)
            {
                return default;
            }
            CancellationTokenSource? cts;
            lock (_stateChangeLock)
            {
                cts = _cts;
                _cts = null;
            }
            if (cts is null)
            {
                return default;
            }

            cts.Cancel();
            cts.Dispose();

            return KnpCollectionHelper.ClearAndDisposeAsync(_connections);
        }

        public void DetachAll()
        {
            if (_cts is null)
            {
                return;
            }

            foreach (KeyValuePair<int, KnpPeerConnection> item in _connections)
            {
                item.Value.Detach();
            }
        }

        async void IThreadPoolWorkItem.Execute()
        {
            CancellationToken cancellationToken;
            lock (_stateChangeLock)
            {
                if (_cts is null)
                {
                    return;
                }
                cancellationToken = _cts.Token;
            }
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);

                    long currentTick = Environment.TickCount64;
                    foreach (KeyValuePair<int, KnpPeerConnection> item in _connections)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (item.Value.IsExpired(currentTick))
                        {
                            if (_connections.TryRemove(item))
                            {
                                await item.Value.DisposeAsync().ConfigureAwait(false);
                            }

                        }
                    }

                }
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
        }

        public KnpPeerConnection? GetOrCreatePeerConnection(int sessionId)
        {
            if (_cts is null)
            {
                return null;
            }
            KnpPeerConnection? connection = _connections.GetOrAdd(sessionId, (id, state) => new KnpPeerConnection(state, id), this);
            if (_cts is null)
            {
                if (_connections.TryRemove(new KeyValuePair<int, KnpPeerConnection>(sessionId, connection)))
                {
                    connection.Dispose();
                }
                return null;
            }
            return connection;
        }

        internal void NotifyPeerConnectionDisposed(int sessionId, KnpPeerConnection connection)
        {
            if (_cts is null)
            {
                return;
            }
            _connections.TryRemove(new KeyValuePair<int, KnpPeerConnection>(sessionId, connection));
        }

    }
}
