using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UnsecuredAPIKeys.Providers.ServerProviders.Services
{
    public class GeolocationResult
    {
        public string Country { get; set; } = "Unknown";
        public string City { get; set; } = "Unknown";
        public string ISP { get; set; } = "Unknown";
        public string ASN { get; set; } = "Unknown";
        public string CloudProvider { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public interface IGeolocationService
    {
        Task<GeolocationResult> GeolocateAsync(string ipAddress);
        bool IsCloudProviderIP(string ipAddress, out string providerName);
    }

    public class GeolocationService : IGeolocationService
    {
        private readonly ILogger<GeolocationService>? _logger;

        private static readonly Dictionary<string, string[]> _cloudSubnets = new()
        {
            ["AWS"] = new[] { "3.5.0.0/16", "52.95.0.0/16", "54.239.0.0/16" },
            ["Azure"] = new[] { "13.64.0.0/16", "40.112.0.0/16", "52.148.0.0/16" },
            ["GCP"] = new[] { "34.64.0.0/16", "35.184.0.0/16", "104.154.0.0/16" },
            ["DigitalOcean"] = new[] { "104.248.0.0/16", "138.68.0.0/16", "159.203.0.0/16" },
            ["Linode"] = new[] { "45.33.0.0/16", "139.162.0.0/16", "172.104.0.0/16" },
            ["Vultr"] = new[] { "45.76.0.0/16", "108.61.0.0/16", "149.28.0.0/16" },
            ["Hetzner"] = new[] { "78.46.0.0/15", "88.198.0.0/16", "95.217.0.0/16" },
            ["OracleCloud"] = new[] { "129.213.0.0/16", "130.35.0.0/16", "140.238.0.0/16" }
        };

        public GeolocationService(ILogger<GeolocationService>? logger = null)
        {
            _logger = logger;
        }

        public async Task<GeolocationResult> GeolocateAsync(string ipAddress)
        {
            try
            {
                if (string.IsNullOrEmpty(ipAddress))
                {
                    return new GeolocationResult { Message = "Geolocation unavailable" };
                }

                if (!IPAddress.TryParse(ipAddress, out var ip))
                {
                    return new GeolocationResult { Message = "Geolocation unavailable" };
                }

                if (IsPrivateIP(ip))
                {
                    return new GeolocationResult
                    {
                        Country = "Private Network",
                        City = "Local",
                        ISP = "Intranet",
                        ASN = "N/A",
                        Message = "Private IP address"
                    };
                }

                // In offline mode we perform cloud provider range checking,
                // and fallback to standard lightweight JSON endpoint when network is active
                string cloudProv = string.Empty;
                IsCloudProviderIP(ipAddress, out cloudProv);

                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetAsync($"http://ip-api.com/json/{ipAddress}");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    // Basic parsing since we don't have NewtonSoft loaded in providers
                    var country = ParseJsonField(content, "country");
                    var city = ParseJsonField(content, "city");
                    var isp = ParseJsonField(content, "isp");
                    var asn = ParseJsonField(content, "as");

                    return new GeolocationResult
                    {
                        Country = string.IsNullOrEmpty(country) ? "Unknown" : country,
                        City = string.IsNullOrEmpty(city) ? "Unknown" : city,
                        ISP = string.IsNullOrEmpty(isp) ? "Unknown" : isp,
                        ASN = string.IsNullOrEmpty(asn) ? "Unknown" : asn,
                        CloudProvider = cloudProv,
                        Message = "Success"
                    };
                }

                return new GeolocationResult
                {
                    Country = "Unknown",
                    CloudProvider = cloudProv,
                    Message = "Geolocation unavailable"
                };
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("Geolocation lookup failed for {IP} - {Error}", ipAddress, ex.Message);
                return new GeolocationResult { Message = "Geolocation unavailable" };
            }
        }

        public bool IsCloudProviderIP(string ipAddress, out string providerName)
        {
            providerName = string.Empty;
            if (string.IsNullOrEmpty(ipAddress) || !IPAddress.TryParse(ipAddress, out var ip))
            {
                return false;
            }

            if (IsPrivateIP(ip))
            {
                return false;
            }

            foreach (var (provider, subnets) in _cloudSubnets)
            {
                foreach (var subnet in subnets)
                {
                    if (IPInSubnet(ip, subnet))
                    {
                        providerName = provider;
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsPrivateIP(IPAddress ip)
        {
            byte[] bytes = ip.GetAddressBytes();
            if (bytes.Length != 4) return false; // IPv4 only for now
            if (bytes[0] == 10) return true; // 10.0.0.0/8
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true; // 172.16.0.0/12
            if (bytes[0] == 192 && bytes[1] == 168) return true; // 192.168.0.0/16
            if (bytes[0] == 127) return true; // 127.0.0.1 loopback
            return false;
        }

        private static bool IPInSubnet(IPAddress ip, string subnet)
        {
            try
            {
                var parts = subnet.Split('/');
                var subnetAddr = IPAddress.Parse(parts[0]);
                int maskLength = parts.Length > 1 ? int.Parse(parts[1]) : 32;

                byte[] ipBytes = ip.GetAddressBytes();
                byte[] subnetBytes = subnetAddr.GetAddressBytes();

                if (ipBytes.Length != subnetBytes.Length) return false;

                int bits = maskLength;
                for (int i = 0; i < ipBytes.Length; i++)
                {
                    if (bits >= 8)
                    {
                        if (ipBytes[i] != subnetBytes[i]) return false;
                        bits -= 8;
                    }
                    else if (bits > 0)
                    {
                        int mask = 0xFF00 >> bits;
                        if ((ipBytes[i] & mask) != (subnetBytes[i] & mask)) return false;
                        break;
                    }
                    else
                    {
                        break;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string ParseJsonField(string json, string field)
        {
            var key = $"\"{field}\":";
            var index = json.IndexOf(key);
            if (index == -1) return string.Empty;

            var start = index + key.Length;
            // Find end of token
            while (start < json.Length && (json[start] == ' ' || json[start] == '"')) start++;

            var end = start;
            while (end < json.Length && json[end] != '"' && json[end] != ',' && json[end] != '}') end++;

            return json.Substring(start, end - start).Trim();
        }
    }
}
