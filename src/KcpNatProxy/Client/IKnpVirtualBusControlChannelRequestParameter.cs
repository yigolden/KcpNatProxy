namespace KcpNatProxy.Client
{
    internal interface IKnpVirtualBusControlChannelRequestParameter<TResult> where TResult : notnull
    {
        KnpVirtualBusControlChannelRequestBase<TResult> CreateRequest();
    }
}
