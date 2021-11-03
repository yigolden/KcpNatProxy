using System;

namespace KcpNatProxy
{
    internal readonly struct KnpServiceBindingRegistration : IDisposable
    {
        private readonly IKnpServiceBindingHolder _holder;
        private readonly IKnpServiceBinding _serviceBinding;

        public KnpServiceBindingRegistration(IKnpServiceBindingHolder holder, IKnpServiceBinding serviceBinding)
        {
            _holder = holder;
            _serviceBinding = serviceBinding;
        }

        public void Dispose()
        {
            if (_holder is not null && _serviceBinding is not null)
            {
                _holder.Remove(_serviceBinding);
            }
        }
    }
}
