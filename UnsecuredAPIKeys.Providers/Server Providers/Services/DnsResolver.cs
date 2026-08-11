using System.Net;
using System.Threading.Tasks;

namespace UnsecuredAPIKeys.Providers.ServerProviders.Services
{
    /// <summary>
    /// Abstraction over DNS resolution to enable deterministic testing
    /// of SSRF protection and DNS-pinning behavior.
    /// </summary>
    public interface IDnsResolver
    {
        Task<IPAddress[]> ResolveAsync(string hostname);
    }

    /// <summary>
    /// Production implementation that delegates to the system DNS resolver.
    /// </summary>
    public class DnsResolver : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string hostname)
        {
            return Dns.GetHostAddressesAsync(hostname);
        }
    }
}
