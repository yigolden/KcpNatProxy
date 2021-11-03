using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace KcpNatProxy.Server
{
    public class KnpServerOptions
    {
        public KnpServerListenOptions[]? Listen { get; set; }
        public string? Credential { get; set; }
        public KnpServiceDescription[]? Services { get; set; }

        [MemberNotNullWhen(true, nameof(Listen), nameof(Services))]
        public bool Validate([NotNullWhen(false)] out string? errorMessage)
        {
            if (Listen is null || Listen.Length == 0)
            {
                errorMessage = "Server listen endpoint is not configured.";
                return false;
            }
            if (Listen.Length != 1)
            {
                errorMessage = "Only one listen endpoint is allowed currently.";
                return false;
            }
            if (!string.IsNullOrEmpty(Credential) && Encoding.UTF8.GetByteCount(Credential) > 64)
            {
                errorMessage = "Credential is too long.";
                return false;
            }
            if (Services is null || Services.Length == 0)
            {
                errorMessage = "No services is configured.";
                return false;
            }

            errorMessage = null;
            return true;
        }
    }

}
