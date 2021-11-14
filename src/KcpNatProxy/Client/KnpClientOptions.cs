using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace KcpNatProxy.Client
{
    public class KnpClientOptions
    {
        public KnpClientConnectOptions? Connect { get; set; }
        public string? Credential { get; set; }
        public KnpProviderDescription[]? Providers { get; set; }
        public KnpServiceDescription[]? Services { get; set; }

        [MemberNotNullWhen(true, nameof(Connect))]
        public bool Validate([NotNullWhen(false)] out string? errorMessage)
        {
            if (Connect is null)
            {
                errorMessage = "Server connect endpoint is not configured.";
                return false;
            }
            if (!string.IsNullOrEmpty(Credential) && Encoding.UTF8.GetByteCount(Credential) > 64)
            {
                errorMessage = "Credential is too long.";
                return false;
            }

            errorMessage = null;
            return true;
        }
    }
}
