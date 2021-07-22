using System;
using System.Net;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal static partial class Log
    {
        [LoggerMessage(EventId = 0x3001, Level = LogLevel.Information, Message = "Connection closed. Reconnect in {seconds} seconds.")]
        public static partial void LogClientTryAgain(ILogger logger, double seconds);

        [LoggerMessage(EventId = 0x3002, Level = LogLevel.Error, Message = "Failed to resolve `{host}` to IP addresses.")]
        public static partial void LogClientNoIPAddress(ILogger logger, string host);

        [LoggerMessage(EventId = 0x3003, Level = LogLevel.Information, Message = "Connecting to `{endPoint}`.")]
        public static partial void LogClientConnecting(ILogger logger, EndPoint endPoint);

        [LoggerMessage(EventId = 0x3004, Level = LogLevel.Debug, Message = "Failed to connect to `{endPoint}`.")]
        public static partial void LogClientConnectionFailed(ILogger logger, Exception ex, EndPoint endPoint);

        [LoggerMessage(EventId = 0x3005, Level = LogLevel.Warning, Message = "Failed to connect to `{endPoint}`.")]
        public static partial void LogClientConnectionFailedCritical(ILogger logger, Exception ex, EndPoint endPoint);

        [LoggerMessage(EventId = 0x3006, Level = LogLevel.Error, Message = "Unhandled exception occurred when receiving packets.")]
        public static partial void LogClientUnhandledExceptionWhenReceiving(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 0x3007, Level = LogLevel.Error, Message = "Unhandled exception occurred when receiving packets.")]
        public static partial void LogClientUnhandledSocketExceptionWhenReceiving(ILogger logger, Exception ex);
    
        [LoggerMessage(EventId = 0x3008, Level = LogLevel.Error, Message = "Unhandled exception occurred when sending heartbeat.")]
        public static partial void LogClientUnhandledExceptionWhenSendingHeartbeat(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 0x3009, Level = LogLevel.Error, Message = "Unhandled exception occurred when sending heartbeat.")]
        public static partial void LogClientUnhandledSocketExceptionWhenSendingHeartbeat(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 0x300A, Level = LogLevel.Information, Message = "Authentication started.")]
        public static partial void LogClientAuthenticationStart(ILogger logger);

        [LoggerMessage(EventId = 0x300B, Level = LogLevel.Warning, Message = "Authentication timed out.")]
        public static partial void LogClientAuthenticationTimeout(ILogger logger);

        [LoggerMessage(EventId = 0x300C, Level = LogLevel.Warning, Message = "Authentication failed. Authentication request is rejected by the server.")]
        public static partial void LogClientAuthenticationMethodNotAllowed(ILogger logger);

        [LoggerMessage(EventId = 0x300D, Level = LogLevel.Warning, Message = "Authentication failed. The server provides a invalid MTU value.")]
        public static partial void LogClientAuthenticationInvalidMtu(ILogger logger);

        [LoggerMessage(EventId = 0x300E, Level = LogLevel.Warning, Message = "Authentication failed. Invalid authentication method is selected by the server.")]
        public static partial void LogClientAuthenticationMethodInvalid(ILogger logger);

        [LoggerMessage(EventId = 0x300F, Level = LogLevel.Warning, Message = "Authentication failed. You credential may be invalid.")]
        public static partial void LogClientAuthenticationCredentialInvalid(ILogger logger);

        [LoggerMessage(EventId = 0x3010, Level = LogLevel.Information, Message = "Authentication succeeded. Negotiated MTU is `{mtu}`.")]
        public static partial void LogClientAuthenticationSuccess(ILogger logger, int mtu);

        [LoggerMessage(EventId = 0x3011, Level = LogLevel.Warning, Message = "No heartbeat is received over a long period of time. The connection may be lost.")]
        public static partial void LogClientTooLongNoHeartbeat(ILogger logger);

        [LoggerMessage(EventId = 0x3012, Level = LogLevel.Warning, Message = "Invalid heartbeat is received. The server is in a wrong state.")]
        public static partial void LogClientReceivedInvalidHeartbeat(ILogger logger);

        [LoggerMessage(EventId = 0x3013, Level = LogLevel.Warning, Message = "Failed to create service `{serviceName}`. The server responded with invalid response.")]
        public static partial void LogClientCreateServiceResponseInvalidPacket(ILogger logger, string serviceName);

        [LoggerMessage(EventId = 0x3014, Level = LogLevel.Warning, Message = "Failed to create service `{serviceName}`. The server can not process this request.")]
        public static partial void LogClientCreateServiceResponseInvalidCommand(ILogger logger, string serviceName);

        [LoggerMessage(EventId = 0x3015, Level = LogLevel.Warning, Message = "Failed to create service `{serviceName}`. This service name is already in use.")]
        public static partial void LogClientCreateServiceResponseNameUnavailable(ILogger logger, string serviceName);

        [LoggerMessage(EventId = 0x3016, Level = LogLevel.Warning, Message = "Failed to create service `{serviceName}`. The endpoint is already used on the server.")]
        public static partial void LogClientCreateServiceResponseEndPointUnavailable(ILogger logger, string serviceName);

        [LoggerMessage(EventId = 0x3017, Level = LogLevel.Warning, Message = "Failed to create service `{serviceName}`. The server refuses to create this service for unknown reason.")]
        public static partial void LogClientCreateServiceResponseUnspecified(ILogger logger, string serviceName);

        [LoggerMessage(EventId = 0x3018, Level = LogLevel.Warning, Message = "Failed to create service `{serviceName}`. The server allocated an invalid service ID.")]
        public static partial void LogClientCreateServiceResponseServiceIdInvalid(ILogger logger, string serviceName);

        [LoggerMessage(EventId = 0x3019, Level = LogLevel.Information, Message = "Service `{serviceName}` is successfully created. The ID of this service is {serviceId}.")]
        public static partial void LogClientCreateServiceSucceeded(ILogger logger, string serviceName, int serviceId);

        [LoggerMessage(EventId = 0x301A, Level = LogLevel.Error, Message = "An unhandled exception occurred when receiving from socket.")]
        public static partial void LogClientUnhandledExceptionInUdpService(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 0x301B, Level = LogLevel.Debug, Message = "Failed to connect to the forward end point. The connect operation timed out.")]
        public static partial void LogClientTcpConnectTimeout(ILogger logger);

        [LoggerMessage(EventId = 0x301C, Level = LogLevel.Debug, Message = "Failed to connect to the forward end point. The connect operation timed out.")]
        public static partial void LogClientTcpConnectFailed(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 0x4001, Level = LogLevel.Information, Message = "Now listening on `{endPoint}`.")]
        public static partial void LogServerStartRunning(ILogger logger, EndPoint endPoint);

        [LoggerMessage(EventId = 0x4002, Level = LogLevel.Error, Message = "A Socket exception occurred when running the server.")]
        public static partial void LogServerSocketException(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 0x4003, Level = LogLevel.Debug, Message = "New connection from client `{endPoint}`.")]
        public static partial void LogServerNewConnection(ILogger logger, EndPoint endPoint);

        [LoggerMessage(EventId = 0x4004, Level = LogLevel.Debug, Message = "Connection from client `{endPoint}` is eliminated.")]
        public static partial void LogServerConnectionEliminated(ILogger logger, EndPoint endPoint);

        [LoggerMessage(EventId = 0x4005, Level = LogLevel.Information, Message = "Authentication succeeded from client `{endPoint}`. Negotiated MTU is `{mtu}`.")]
        public static partial void LogServerAuthenticationSuccess(ILogger logger, EndPoint endPoint, int mtu);

        [LoggerMessage(EventId = 0x4006, Level = LogLevel.Error, Message = "An error occurred when sending packets.")]
        public static partial void LogServerSocketSendError(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 0x4007, Level = LogLevel.Information, Message = "TCP Service `{serviceName}` is created. Listening on `{endPoint}`.")]
        public static partial void LogServerTcpServiceCreated(ILogger logger, string serviceName, EndPoint endPoint);

        [LoggerMessage(EventId = 0x4008, Level = LogLevel.Information, Message = "UDP Service `{serviceName}` is created. Listening on `{endPoint}`.")]
        public static partial void LogServerUdpServiceCreated(ILogger logger, string serviceName, EndPoint endPoint);

        [LoggerMessage(EventId = 0x4009, Level = LogLevel.Error, Message = "An unhandled exception occurred when processing control channel.")]
        public static partial void LogUnhandledExceptionInControlChannelProcessing(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 0x400A, Level = LogLevel.Information, Message = "TCP Service `{serviceName}` is destroyed.")]
        public static partial void LogServerTcpServiceDestroyed(ILogger logger, string serviceName);

        [LoggerMessage(EventId = 0x400B, Level = LogLevel.Information, Message = "UDP Service `{serviceName}` is destroyed.")]
        public static partial void LogServerUdpServiceDestroyed(ILogger logger, string serviceName);

        [LoggerMessage(EventId = 0x400C, Level = LogLevel.Error, Message = "An exception occurred when forwarding data for TCP service.")]
        public static partial void LogServerTcpTransportUnhandledException(ILogger logger, Exception ex);
    }
}
