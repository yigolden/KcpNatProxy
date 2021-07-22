using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace KcpNatProxy
{
    internal sealed class ListenEndPointTracker
    {
        private int _nextId;
        private readonly ConcurrentDictionary<ServiceEndPoint, int> _endPoints = new ConcurrentDictionary<ServiceEndPoint, int>();

        public KcpListenEndPointTrackerHolder Track(KcpNatProxyServiceType type, EndPoint endPoint)
        {
            if (endPoint is null)
            {
                throw new ArgumentNullException(nameof(endPoint));
            }

            var ep = new ServiceEndPoint(type, endPoint);
            int id = Interlocked.Increment(ref _nextId);
            int addedId = _endPoints.AddOrUpdate(ep, (e, i) => i, (e, o, i) => o, id);
            if (id == addedId)
            {
                return new KcpListenEndPointTrackerHolder(this, ep, id);
            }
            return default;
        }

        public void Release(ServiceEndPoint endPoint, int id)
        {
            if (endPoint is null)
            {
                return;
            }
            _endPoints.TryRemove(new KeyValuePair<ServiceEndPoint, int>(endPoint, id));
        }
    }

    internal readonly struct KcpListenEndPointTrackerHolder : IDisposable
    {
        private readonly ListenEndPointTracker _tracker;
        private readonly ServiceEndPoint _endPoint;
        private readonly int _id;

        public KcpListenEndPointTrackerHolder(ListenEndPointTracker tracker, ServiceEndPoint endPoint, int id)
        {
            _tracker = tracker;
            _endPoint = endPoint;
            _id = id;
        }

        public bool IsTracking => _endPoint is not null;

        public void Dispose()
        {
            if (_tracker is not null)
            {
                _tracker.Release(_endPoint, _id);
            }
        }
    }
}
