using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy
{
    internal sealed class UdpLivenessTracker : IDisposable
    {
        private readonly ConcurrentDictionary<EndPoint, DateTime> _endpoints = new();
        private CancellationTokenSource? _cts;

        private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(1);

        public UdpLivenessTracker()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ScanLoop(_cts));
        }

        public void Track(EndPoint endPoint)
        {
            if (_cts is null)
            {
                return;
            }
            _endpoints.AddOrUpdate(endPoint, DateTime.UtcNow, (_, _) => DateTime.UtcNow);
            if (Volatile.Read(ref _cts) is null)
            {
                _endpoints.TryRemove(endPoint, out _);
            }
        }

        public bool Check(EndPoint endPoint)
        {
            if (_cts is null)
            {
                return false;
            }
            if (_endpoints.TryGetValue(endPoint, out DateTime dateTime))
            {
                if ((DateTime.UtcNow - Threshold) < dateTime)
                {
                    return true;
                }
                else
                {
                    _endpoints.TryRemove(new KeyValuePair<EndPoint, DateTime>(endPoint, dateTime));
                }
            }
            return false;
        }

        private async Task ScanLoop(CancellationTokenSource cts)
        {
            CancellationToken cancellationToken = cts.Token;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);

                    DateTime threshold = DateTime.UtcNow - Threshold;
                    foreach (KeyValuePair<EndPoint, DateTime> item in _endpoints)
                    {
                        if (item.Value <= threshold)
                        {
                            _endpoints.TryRemove(item);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            finally
            {
                _endpoints.Clear();
            }
        }

        public void Dispose()
        {
            CancellationTokenSource? cts = _cts;
            if (cts is not null)
            {
                _cts = null;
                cts.Cancel();
            }
        }
    }
}
