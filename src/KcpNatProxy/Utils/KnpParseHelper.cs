using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace KcpNatProxy
{
    internal static class KnpParseHelper
    {
        internal static bool TryParseName(ReadOnlySpan<byte> buffer, out int bytesRead, [NotNullWhen(true)] out string? name)
        {
            if (buffer.IsEmpty)
            {
                name = null;
                bytesRead = 0;
                return false;
            }
            int nameLength = buffer[0];
            if ((nameLength + 1) > buffer.Length)
            {
                name = null;
                bytesRead = 1;
                return false;
            }
            try
            {
                name = Encoding.UTF8.GetString(buffer.Slice(1, nameLength));
                bytesRead = nameLength + 1;
                return true;
            }
            catch
            {
                name = null;
                bytesRead = nameLength + 1;
                return false;
            }
        }
    }
}
