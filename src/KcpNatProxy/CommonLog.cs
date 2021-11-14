using System;
using System.Net;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal static partial class Log
    {
        [LoggerMessage(EventId = 0x3001, EventName = "ServerUnhandledException", Level = LogLevel.Error, Message = "Unhandled exception occurred.")]
        internal static partial void LogCommonUnhandledException(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 0x3002, EventName = "ServerUdpServiceStarted", Level = LogLevel.Information, Message = "Service `{name}` started. Listening on UDP endpoint `{endPoint}`")]
        internal static partial void LogCommonUdpServiceStarted(ILogger logger, string name, EndPoint endPoint);

        [LoggerMessage(EventId = 0x3003, EventName = "ServerTcpServiceStarted", Level = LogLevel.Information, Message = "Service `{name}` started. Listening on TCP endpoint `{endPoint}`")]
        internal static partial void LogCommonTcpServiceStarted(ILogger logger, string name, EndPoint endPoint);

        [LoggerMessage(EventId = 0x3004, EventName = "ServerTcpServiceStarted", Level = LogLevel.Information, Message = "Virtual bus `{name}` started. Relay type: {relayType}.")]
        internal static partial void LogCommonVirtualBusServiceStarted(ILogger logger, string name, KnpVirtualBusRelayType relayType);

        [LoggerMessage(EventId = 0x3005, EventName = "CommonServiceBindingQueryFound", Level = LogLevel.Trace, Message = "Service binding selected for `{serviceName}`. Binding id: {bindingId}")]
        internal static partial void LogCommonServiceBindingQueryFound(ILogger logger, string serviceName, int bindingId);

        [LoggerMessage(EventId = 0x3006, EventName = "CommonServiceBindingQueryNotFound", Level = LogLevel.Trace, Message = "Service binding not found for `{serviceName}`.")]
        internal static partial void LogCommonServiceBindingQueryNotFound(ILogger logger, string serviceName);

        [LoggerMessage(EventId = 0x3007, EventName = "CommonTcpConnectionAccepting", Level = LogLevel.Trace, Message = "Service `{serviceName}` is waiting for TCP connection.")]
        internal static partial void LogCommonTcpConnectionAccepting(ILogger logger, string serviceName);

        [LoggerMessage(EventId = 0x3008, EventName = "CommonTcpConnectionAccepted", Level = LogLevel.Trace, Message = "Service `{serviceName}` accepted TCP connection from `{remoteEndPoint}`.")]
        internal static partial void LogCommonTcpConnectionAccepted(ILogger logger, string serviceName, EndPoint? remoteEndPoint);

        [LoggerMessage(EventId = 0x3009, EventName = "CommonUdpPacketReceived", Level = LogLevel.Trace, Message = "Service `{serviceName}` received UDP packet from `{remoteEndPoint}`. Packet size: {packetSize}")]
        internal static partial void LogCommonUdpPacketReceived(ILogger logger, string serviceName, EndPoint? remoteEndPoint, int packetSize);
    }
}
