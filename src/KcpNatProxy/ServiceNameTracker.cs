using System;
using System.Collections.Generic;
using System.Threading;

namespace KcpNatProxy
{
    internal sealed class ServiceNameTracker
    {
        private HashSet<string> _names = new HashSet<string>(StringComparer.Ordinal);

        public ServiceNameHandle? Track(string name)
        {
            if (name is null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            lock (_names)
            {
                if (_names.TryGetValue(name, out _))
                {
                    return null;
                }
                ServiceNameHandle handle = new ServiceNameHandle(this, name);
                _names.Add(name);
                return handle;
            }
        }

        public void Remove(string name)
        {
            lock (_names)
            {
                _names.Remove(name);
            }
        }
    }

    internal sealed class ServiceNameHandle : IDisposable
    {
        private readonly ServiceNameTracker _tracker;
        private string? _name;

        public string Name => _name ?? string.Empty;

        public ServiceNameHandle(ServiceNameTracker tracker, string name)
        {
            _tracker = tracker;
            _name = name ?? throw new ArgumentNullException(nameof(name));
        }

        public void Dispose()
        {
            string? name = Interlocked.Exchange(ref _name, null);
            if (name is not null)
            {
                _tracker.Remove(name);
            }
        }
    }
}
