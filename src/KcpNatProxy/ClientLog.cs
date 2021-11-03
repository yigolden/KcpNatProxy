using System;
using System.Net;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal static partial class Log
    {
        [LoggerMessage(EventId = 0x1001, EventName = "ClientConnecting", Level = LogLevel.Debug, Message = "Connecting to server.")]
        internal static partial void LogClientConnecting(ILogger logger);

        [LoggerMessage(EventId = 0x1002, EventName = "ClientP2PModeAlert", Level = LogLevel.Information, Message = "At least one virtual bus is configured. To enable P2P connection for virtual bus services, make sure this program is added to the exclusion list of the firewall.")]
        internal static partial void LogClientP2PModeAlert(ILogger logger);

        [LoggerMessage(EventId = 0x1003, EventName = "ClientHostNameResolved", Level = LogLevel.Debug, Message = "Host name `{hostName}` is resolved to {address}.")]
        internal static partial void LogClientHostNameResolved(ILogger logger, string hostName, IPAddress address);

        [LoggerMessage(EventId = 0x1004, EventName = "ClientHostNameResolvingFailed", Level = LogLevel.Warning, Message = "Failed to resove host name `{hostName}`.")]
        internal static partial void LogClientHostNameResolvingFailed(ILogger logger, string hostName, Exception ex);

        [LoggerMessage(EventId = 0x1005, EventName = "ClientConnectFailed", Level = LogLevel.Debug, Message = "Failed to connect to server `{endPoint}`.")]
        internal static partial void LogClientConnectFailed(ILogger logger, EndPoint endPoint);

        [LoggerMessage(EventId = 0x1006, EventName = "ClientAuthenticationFailed", Level = LogLevel.Error, Message = "Authentication failed.")]
        internal static partial void LogClientAuthenticationFailed(ILogger logger);

        [LoggerMessage(EventId = 0x1007, EventName = "ClientAuthenticationTimeout", Level = LogLevel.Error, Message = "Authentication timed out.")]
        internal static partial void LogClientAuthenticationTimeout(ILogger logger);

        [LoggerMessage(EventId = 0x1008, EventName = "ClientConnected", Level = LogLevel.Information, Message = "Connected to server. Session id: {sessionId}")]
        internal static partial void LogClientConnected(ILogger logger, int sessionId);

        [LoggerMessage(EventId = 0x1009, EventName = "ClientUnhandledException", Level = LogLevel.Error, Message = "Unhandled exception occurred.")]
        internal static partial void LogClientUnhandledException(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 0x100A, EventName = "ClientConnectRetry", Level = LogLevel.Debug, Message = "Retry in {seconds} seconds.")]
        internal static partial void LogClientConnectRetry(ILogger logger, int seconds);

        [LoggerMessage(EventId = 0x100B, EventName = "ClientConnectionLost", Level = LogLevel.Warning, Message = "Connection to server is lost.")]
        internal static partial void LogClientConnectionLost(ILogger logger);

        [LoggerMessage(EventId = 0x100C, EventName = "ClientBindProvidersFailed", Level = LogLevel.Warning, Message = "Failed to bind providers. Connection will be closed.")]
        internal static partial void LogClientBindProvidersFailed(ILogger logger);

        [LoggerMessage(EventId = 0x100D, EventName = "ClientBindProviderInvalidResponse", Level = LogLevel.Debug, Message = "Received invalid response when binding provider `{name}`.")]
        internal static partial void LogClientBindProviderInvalidResponse(ILogger logger, string name);

        [LoggerMessage(EventId = 0x100E, EventName = "ClientBindProviderUnsupportedCommand", Level = LogLevel.Warning, Message = "Failed to bind provider `{name}`. Server responded with unsupported command error.")]
        internal static partial void LogClientBindProviderUnsupportedCommand(ILogger logger, string name);

        [LoggerMessage(EventId = 0x100F, EventName = "ClientBindProviderUnsupportedServiceType", Level = LogLevel.Warning, Message = "Failed to bind provider `{name}`. Server responded with unsupported service type error.")]
        internal static partial void LogClientBindProviderUnsupportedServiceType(ILogger logger, string name);

        [LoggerMessage(EventId = 0x1010, EventName = "ClientBindProviderServiceNotFound", Level = LogLevel.Warning, Message = "Failed to bind provider `{name}`. Server responded with service not found error.")]
        internal static partial void LogClientBindProviderServiceNotFound(ILogger logger, string name);

        [LoggerMessage(EventId = 0x1011, EventName = "ClientBindProviderSucceeded", Level = LogLevel.Information, Message = "Binding provider `{name}` succeeded.")]
        internal static partial void LogClientBindProviderSucceeded(ILogger logger, string name);


        [LoggerMessage(EventId = 0x1012, EventName = "ClientUdpServiceStarted", Level = LogLevel.Information, Message = "Service `{name}` on virtual bus `{virtualBusName}` started. Listening on UDP endpoint `{endPoint}`")]
        internal static partial void LogClientUdpServiceStarted(ILogger logger, string name, string virtualBusName, EndPoint endPoint);

        [LoggerMessage(EventId = 0x1013, EventName = "ClientTcpServiceStarted", Level = LogLevel.Information, Message = "Service `{name}` on virtual bus `{virtualBusName}` started. Listening on TCP endpoint `{endPoint}`")]
        internal static partial void LogClientTcpServiceStarted(ILogger logger, string name, string virtualBusName, EndPoint endPoint);


        [LoggerMessage(EventId = 0x1014, EventName = "ClientBindServiceBusNotFound", Level = LogLevel.Warning, Message = "Failed to bind service bus `{name}`. Server responded with service not found error.")]
        internal static partial void LogClientBindServiceBusNotFound(ILogger logger, string name);

        [LoggerMessage(EventId = 0x1015, EventName = "ClientBindServiceBusInvalidResponse", Level = LogLevel.Debug, Message = "Received invalid response when binding service bus `{name}`.")]
        internal static partial void LogClientBindServiceBusInvalidResponse(ILogger logger, string name);

        [LoggerMessage(EventId = 0x1016, EventName = "ClientBindServiceBusSucceeded", Level = LogLevel.Information, Message = "Binding service bus `{name}` succeeded.")]
        internal static partial void LogClientBindServiceBusSucceeded(ILogger logger, string name);

        [LoggerMessage(EventId = 0x1017, EventName = "ClientVirtualBusProviderAdded", Level = LogLevel.Debug, Message = "Provider `{name}` ({serviceType}) is added to virtual bus `{virtualBus}`.")]
        internal static partial void LogClientVirtualBusProviderAdded(ILogger logger, string virtualBus, string name, KnpServiceType serviceType);
    }
}
