using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KcpNatProxy
{
    internal static class KnpCollectionHelper
    {
        public static void ClearAndDispose<TKey, TValue>(ConcurrentDictionary<TKey, TValue> dictionary) where TKey : notnull where TValue : IDisposable
        {
            while (!dictionary.IsEmpty)
            {
                foreach (KeyValuePair<TKey, TValue> item in dictionary)
                {
                    if (dictionary.TryRemove(item))
                    {
                        item.Value.Dispose();
                    }
                }
            }
        }

        public static async ValueTask ClearAndDisposeAsync<TKey, TValue>(ConcurrentDictionary<TKey, TValue> dictionary) where TKey : notnull where TValue : IAsyncDisposable
        {
            while (!dictionary.IsEmpty)
            {
                foreach (KeyValuePair<TKey, TValue> item in dictionary)
                {
                    if (dictionary.TryRemove(item))
                    {
                        await item.Value.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }

        public static void Clear<TKey, TValue>(ConcurrentDictionary<TKey, TValue> dictionary, Action<TValue> clearAction) where TKey : notnull
        {
            while (!dictionary.IsEmpty)
            {
                foreach (KeyValuePair<TKey, TValue> item in dictionary)
                {
                    if (dictionary.TryRemove(item))
                    {
                        clearAction.Invoke(item.Value);
                    }
                }
            }
        }

        public static void Clear<TKey, TValue, TState>(ConcurrentDictionary<TKey, TValue> dictionary, Action<TValue, TState> clearAction, TState state) where TKey : notnull
        {
            while (!dictionary.IsEmpty)
            {
                foreach (KeyValuePair<TKey, TValue> item in dictionary)
                {
                    if (dictionary.TryRemove(item))
                    {
                        clearAction.Invoke(item.Value, state);
                    }
                }
            }
        }
    }
}
