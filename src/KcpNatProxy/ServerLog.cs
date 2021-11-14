using System;
using System.Net;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal static partial class Log
    {
        [LoggerMessage(EventId = 0x2001, EventName = "ServerListening", Level = LogLevel.Information, Message = "Listening on: {endpoint}")]
        internal static partial void LogServerListening(ILogger logger, EndPoint endpoint);

        [LoggerMessage(EventId = 0x2002, EventName = "ServerConnectionAccepted", Level = LogLevel.Debug, Message = "Accepted connection from: {endpoint}. Assigned session id: {sessionId}")]
        internal static partial void LogServerConnectionAccepted(ILogger logger, EndPoint? endpoint, int sessionId);

        [LoggerMessage(EventId = 0x2003, EventName = "ServerListeningFailed", Level = LogLevel.Error, Message = "Failed to accept connections.")]
        internal static partial void LogServerListeningFailed(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 0x2004, EventName = "ServerUnhandledException", Level = LogLevel.Error, Message = "Unhandled exception occurred.")]
        internal static partial void LogServerUnhandledException(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 0x2005, EventName = "ServerSessionAuthenticated", Level = LogLevel.Information, Message = "Client authenticated. Session id: {sessionId}. Remote endpoint: {endpoint}")]
        internal static partial void LogServerSessionAuthenticated(ILogger logger, EndPoint? endpoint, int sessionId);

        [LoggerMessage(EventId = 0x2006, EventName = "ServerSessionAuthenticationFailed", Level = LogLevel.Debug, Message = "Client authentication failed. Remote endpoint: {endpoint}")]
        internal static partial void LogServerSessionAuthenticationFailed(ILogger logger, EndPoint? endpoint);

        [LoggerMessage(EventId = 0x2007, EventName = "ServerSessionAuthenticationTimeout", Level = LogLevel.Debug, Message = "Client authentication timed out. Remote endpoint: {endpoint}")]
        internal static partial void LogServerSessionAuthenticationTimeout(ILogger logger, EndPoint? endpoint);

        [LoggerMessage(EventId = 0x2008, EventName = "ServerSessionClosedUnauthenticated", Level = LogLevel.Debug, Message = "Client session closed. Session id: {sessionId}")]
        internal static partial void LogServerSessionClosedUnauthenticated(ILogger logger, int sessionId);

        [LoggerMessage(EventId = 0x2009, EventName = "ServerSessionClosed", Level = LogLevel.Information, Message = "Client session closed. Session id: {sessionId}")]
        internal static partial void LogServerSessionClosed(ILogger logger, int sessionId);

        [LoggerMessage(EventId = 0x200A, EventName = "ServerRequestInvalid", Level = LogLevel.Debug, Message = "Received invalid request from client. Session id: {sessionId}")]
        internal static partial void LogServerRequestInvalid(ILogger logger, int sessionId);

        [LoggerMessage(EventId = 0x200B, EventName = "ServerRequestUnsupportedCommand", Level = LogLevel.Debug, Message = "Received unsupported command from client. Session id: {sessionId}")]
        internal static partial void LogServerRequestUnsupportedCommand(ILogger logger, int sessionId);

        [LoggerMessage(EventId = 0x200C, EventName = "ServerRequestUnknownServiceType", Level = LogLevel.Debug, Message = "Received command of unknown service type from client. Session id: {sessionId}")]
        internal static partial void LogServerRequestUnknownServiceType(ILogger logger, int sessionId);

        [LoggerMessage(EventId = 0x200D, EventName = "ServerRequestServiceNotFound", Level = LogLevel.Debug, Message = "Can not bind to service `{name}`. Service is not found or service type does not match. Session id: {sessionId}")]
        internal static partial void LogServerRequestServiceNotFound(ILogger logger, string name, int sessionId);

        [LoggerMessage(EventId = 0x200E, EventName = "ServerRequestBindingFailed", Level = LogLevel.Debug, Message = "Can not bind to service `{name}`. Failed to create service binding. Session id: {sessionId}")]
        internal static partial void LogServerRequestBindingFailed(ILogger logger, string name, int sessionId);

        [LoggerMessage(EventId = 0x200F, EventName = "ServerBindingCreated", Level = LogLevel.Information, Message = "Service binding created. Service name: `{name}`. Session id: {sessionId}")]
        internal static partial void LogServerBindingCreated(ILogger logger, string name, int sessionId);

        [LoggerMessage(EventId = 0x2010, EventName = "ServerBindingDestroyed", Level = LogLevel.Information, Message = "Service binding destroyed. Service name: `{name}`. Session id: {sessionId}")]
        internal static partial void LogServerBindingDestroyed(ILogger logger, string name, int sessionId);

        [LoggerMessage(EventId = 0x2011, EventName = "ServerRequestVirtualBusNotFound", Level = LogLevel.Debug, Message = "Can not bind to virtual bus `{name}`. Virtual bus is not found. Session id: {sessionId}")]
        internal static partial void LogServerRequestVirtualBusNotFound(ILogger logger, string name, int sessionId);

        [LoggerMessage(EventId = 0x2012, EventName = "ServerRequestVirtualBusBindingFailed", Level = LogLevel.Debug, Message = "Can not bind to virtual bus `{name}`. Failed to create service binding. Session id: {sessionId}")]
        internal static partial void LogServerRequestVirtualBusBindingFailed(ILogger logger, string name, int sessionId);

        [LoggerMessage(EventId = 0x2013, EventName = "ServerVirtualBusBindingCreated", Level = LogLevel.Debug, Message = "Client is bound to virtual bus `{name}`. Session id: {sessionId}")]
        internal static partial void LogServerVirtualBusBindingCreated(ILogger logger, string name, int sessionId);

        [LoggerMessage(EventId = 0x2014, EventName = "ServerVirtualBusProviderAdded", Level = LogLevel.Debug, Message = "Provider `{name}` ({serviceType}) is added to virtual bus `{virtualBus}`. Session id: {sessionId}")]
        internal static partial void LogServerVirtualBusProviderAdded(ILogger logger, string virtualBus, string name, KnpServiceType serviceType, int sessionId);

        [LoggerMessage(EventId = 0x2015, EventName = "ServerVirtualBusProviderRemoved", Level = LogLevel.Debug, Message = "Provider `{name}` ({serviceType}) is removed from virtual bus `{virtualBus}`. Session id: {sessionId}")]
        internal static partial void LogServerVirtualBusProviderRemoved(ILogger logger, string virtualBus, string name, KnpServiceType serviceType, int sessionId);
    }
}
