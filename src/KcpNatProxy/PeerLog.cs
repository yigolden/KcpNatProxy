using System.Net;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal static partial class Log
    {
        [LoggerMessage(EventId = 0x4001, EventName = "PeerConnectionCreating", Level = LogLevel.Debug, Message = "Creating peer connection to client {sessionId}.")]
        internal static partial void LogPeerConnectionCreating(ILogger logger, int sessionId);


        [LoggerMessage(EventId = 0x4002, EventName = "PeerServerRelayConnectionNegotiating", Level = LogLevel.Debug, Message = "Start negotiating for server relay connection to session {sessionId}.")]
        internal static partial void LogPeerServerRelayConnectionNegotiating(ILogger logger, int sessionId);

        [LoggerMessage(EventId = 0x4003, EventName = "PeerServerRelayConnectionConnected", Level = LogLevel.Information, Message = "Server relay for peer connection {sessionId} established. Negotiated MTU: {mtu}")]
        internal static partial void LogPeerServerRelayConnectionConnected(ILogger logger, int sessionId, int mtu);

        [LoggerMessage(EventId = 0x4004, EventName = "PeerServerRelayConnectionNegotiationFailed", Level = LogLevel.Warning, Message = "Failed to create server relay for peer connection {sessionId}. Negotiation failed.")]
        internal static partial void LogPeerServerRelayConnectionNegotiationFailed(ILogger logger, int sessionId);

        [LoggerMessage(EventId = 0x4005, EventName = "PeerServerRelayConnectionClosed", Level = LogLevel.Debug, Message = "Server relay for peer connection {sessionId} closed.")]
        internal static partial void LogPeerServerRelayConnectionClosed(ILogger logger, int sessionId);


        [LoggerMessage(EventId = 0x4006, EventName = "PeerServerReflexConnectionCreatingInQueryMode", Level = LogLevel.Debug, Message = "Creating direct peer connection to session {sessionId} in query mode.")]
        internal static partial void LogPeerServerReflexConnectionCreatingInQueryMode(ILogger logger, int sessionId);

        [LoggerMessage(EventId = 0x4007, EventName = "PeerServerReflexConnectionCreatingInConnectMode", Level = LogLevel.Debug, Message = "Creating direct peer connection to session {sessionId} in connect mode. EndPoint: {endPoint}")]
        internal static partial void LogPeerServerReflexConnectionCreatingInConnectMode(ILogger logger, int sessionId, EndPoint endPoint);

        [LoggerMessage(EventId = 0x4008, EventName = "PeerServerReflexConnectionQuerying", Level = LogLevel.Trace, Message = "Start querying for direct connection endpoint to session {sessionId}.")]
        internal static partial void LogPeerServerReflexConnectionQuerying(ILogger logger, int sessionId);

        [LoggerMessage(EventId = 0x4009, EventName = "PeerServerReflexConnectionQueryComplete", Level = LogLevel.Trace, Message = "Completed querying for direct connection endpoint to session {sessionId}. EndPoint: {remoteEndPoint}")]
        internal static partial void LogPeerServerReflexConnectionQueryComplete(ILogger logger, int sessionId, EndPoint remoteEndPoint);

        [LoggerMessage(EventId = 0x400A, EventName = "PeerServerReflexConnectionProbingStarted", Level = LogLevel.Debug, Message = "Start probing for client session {sessionId} at `{remoteEndPoint}`.")]
        internal static partial void LogPeerServerReflexConnectionProbingStarted(ILogger logger, int sessionId, EndPoint remoteEndPoint);

        [LoggerMessage(EventId = 0x400B, EventName = "PeerServerReflexConnectionProbingUnresponsive", Level = LogLevel.Debug, Message = "Probing for client session {sessionId} ended. No response received from `{remoteEndPoint}`.")]
        internal static partial void LogPeerServerReflexConnectionProbingUnresponsive(ILogger logger, int sessionId, EndPoint remoteEndPoint);

        [LoggerMessage(EventId = 0x400C, EventName = "PeerServerReflexConnectionProbingReceived", Level = LogLevel.Debug, Message = "Received peer probing from `{remoteEndPoint}`. Remote session id: {sessionId}")]
        internal static partial void LogPeerServerReflexConnectionProbingReceived(ILogger logger, EndPoint remoteEndPoint, int sessionId);

        [LoggerMessage(EventId = 0x400D, EventName = "PeerServerReflexConnectionNegotiating", Level = LogLevel.Debug, Message = "Start negotiating for direct connection to session {sessionId}.")]
        internal static partial void LogPeerServerReflexConnectionNegotiating(ILogger logger, int sessionId);

        [LoggerMessage(EventId = 0x400E, EventName = "PeerServerReflexConnectionConnected", Level = LogLevel.Information, Message = "Direct connection to peer {sessionId} established. RemoteEndPoint: `{endPoint}`. Negotiated MTU: {mtu}.")]
        internal static partial void LogPeerServerReflexConnectionConnected(ILogger logger, int sessionId, EndPoint? endPoint, int mtu);

        [LoggerMessage(EventId = 0x400F, EventName = "PeerServerReflexConnectionNegotiationFailed", Level = LogLevel.Warning, Message = "Failed to create direct connection to peer {sessionId}. Negotiation failed.")]
        internal static partial void LogPeerServerReflexConnectionNegotiationFailed(ILogger logger, int sessionId);

        [LoggerMessage(EventId = 0x4010, EventName = "PeerServerReflexConnectionNegotiationTimeout", Level = LogLevel.Warning, Message = "Failed to create direct connection to peer {sessionId}. Negotiation timed out.")]
        internal static partial void LogPeerServerReflexConnectionNegotiationTimeout(ILogger logger, int sessionId);

        [LoggerMessage(EventId = 0x4011, EventName = "PeerServerReflexConnectionClosed", Level = LogLevel.Debug, Message = "Direct connection to peer {sessionId} closed.")]
        internal static partial void LogPeerServerReflexConnectionClosed(ILogger logger, int sessionId);


        [LoggerMessage(EventId = 0x4012, EventName = "PeerVirtualBusProviderParameterNotificationReceived", Level = LogLevel.Trace, Message = "Received peer notification from server. Virtual bus: `{virtualBus}`. Session id: {sessionId}. Type: provider parameter information.")]
        internal static partial void LogPeerVirtualBusProviderParameterNotificationReceived(ILogger logger, string virtualBus, int sessionId);

        [LoggerMessage(EventId = 0x4013, EventName = "PeerVirtualBusPeerConnectNotificationReceived", Level = LogLevel.Trace, Message = "Received peer notification from server. Virtual bus: `{virtualBus}`. Session id: {sessionId}. Type: peer connection request.")]
        internal static partial void LogPeerVirtualBusPeerConnectNotificationReceived(ILogger logger, string virtualBus, int sessionId);

    }
}
