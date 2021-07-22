using System;
using System.Net;

namespace KcpNatProxy
{
    internal sealed class ServiceEndPoint : IEquatable<ServiceEndPoint>
    {
        public KcpNatProxyServiceType Type { get; }
        public EndPoint ListenEndPoint { get; }

        public ServiceEndPoint(KcpNatProxyServiceType type, EndPoint listenEndPoint)
        {
            Type = type;
            ListenEndPoint = listenEndPoint;
        }

        public bool Equals(ServiceEndPoint? other) => other is not null && other.Type == Type && other.ListenEndPoint.Equals(ListenEndPoint);

        public override bool Equals(object? obj)
        {
            return Equals(obj as ServiceEndPoint);
        }

        public override int GetHashCode() => HashCode.Combine(Type, ListenEndPoint);
    }
}
