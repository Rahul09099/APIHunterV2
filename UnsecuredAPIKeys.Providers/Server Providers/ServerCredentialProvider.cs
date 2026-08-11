using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;
using UnsecuredAPIKeys.Providers.ServerProviders.Services;

namespace UnsecuredAPIKeys.Providers.ServerProviders
{
    [ApiProvider]
    public class ServerCredentialProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Server Credentials";
        public override ApiTypeEnum ApiType => ApiTypeEnum.ServerCredential;

        private readonly INetworkVerifier _networkVerifier;
        private readonly IAuthenticationVerifier _authVerifier;
        private readonly IContextExtractor _contextExtractor;
        private readonly IEntropyAnalyzer _entropyAnalyzer;
        private readonly IOSINTService _osintService;
        private readonly IGeolocationService _geolocationService;
        private readonly IDnsResolver _dnsResolver;

        // Parameterless constructor for auto-discovery
        public ServerCredentialProvider() : base(null)
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            _contextExtractor = new ContextExtractor();
            _entropyAnalyzer = new EntropyAnalyzer();
            _networkVerifier = new NetworkVerifier();
            _authVerifier = new AuthenticationVerifier(cache);
            _osintService = new OSINTService(cache);
            _geolocationService = new GeolocationService();
            _dnsResolver = new DnsResolver();
        }

        // DI-enabled constructor
        public ServerCredentialProvider(
            INetworkVerifier networkVerifier,
            IAuthenticationVerifier authVerifier,
            IContextExtractor contextExtractor,
            IEntropyAnalyzer entropyAnalyzer,
            IOSINTService osintService,
            IGeolocationService geolocationService,
            IDnsResolver? dnsResolver = null,
            ILogger<ServerCredentialProvider>? logger = null) : base(logger)
        {
            _networkVerifier = networkVerifier;
            _authVerifier = authVerifier;
            _contextExtractor = contextExtractor;
            _entropyAnalyzer = entropyAnalyzer;
            _osintService = osintService;
            _geolocationService = geolocationService;
            _dnsResolver = dnsResolver ?? new DnsResolver();
        }

        public override IEnumerable<string> RegexPatterns =>
        [
            // SSH Credentials & Private Keys
            @"ssh\s+([a-zA-Z0-9._%+\\-]+)@([a-zA-Z0-9.-]+|\[[a-fA-F0-9:]+\])",
            @"-----BEGIN\s+.*PRIVATE\s+KEY-----",
            
            // FTP/SFTP/FTPS URIs (supporting complex usernames & bracketed IPv6)
            @"(ftp|sftp|ftps)://([a-zA-Z0-9._%+\\-]+):([^@\s]+)@([a-zA-Z0-9.-]+|\[[a-fA-F0-9:]+\])(?::(\d+))?",
            
            // Database Connection URIs & ADO.NET Strings
            @"(mysql|postgresql|postgres|mongodb)://([a-zA-Z0-9._%+\\-]+):([^@\s]+)@([a-zA-Z0-9.-]+|\[[a-fA-F0-9:]+\])(?::(\d+))?(?:/([a-zA-Z0-9._-]+))?",
            @"redis://(?::([^@\s]+)@)?([a-zA-Z0-9.-]+|\[[a-fA-F0-9:]+\])(?::(\d+))?",
            @"Server=([a-zA-Z0-9.-]+);Database=([a-zA-Z0-9_-]+);User Id=([a-zA-Z0-9._%+\\-]+);Password=([^;]+);",
            
            // RDP & Remote Access
            @"rdp://([a-zA-Z0-9._%+\\-]+):([^@\s]+)@([a-zA-Z0-9.-]+|\[[a-fA-F0-9:]+\])(?::(\d+))?",
            
            // SMTP & Email Servers
            @"smtp://([a-zA-Z0-9._%+\\-]+):([^@\s]+)@([a-zA-Z0-9.-]+|\[[a-fA-F0-9:]+\])(?::(\d+))?",

            // cPanel, WHM & Plesk Server Management URIs
            @"(cpanel|whm|plesk)://([a-zA-Z0-9._%+\\-]+):([^@\s]+)@([a-zA-Z0-9.-]+|\[[a-fA-F0-9:]+\])(?::(\d+))?"
        ];

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey);
        }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // 1. Parse matched credential details cleanly
                var (cred, rawPassword) = ParseCredentialAndGetRawPassword(apiKey);

                if (cred == null || string.IsNullOrWhiteSpace(cred.Host))
                {
                    return ValidationResult.HasHttpError(HttpStatusCode.BadRequest, "Invalid or unparseable server credential format.");
                }

                // 2. Handle SSH Private Keys (Header detection without Network Connects)
                if (cred.Host == "HeaderDetectedOnly")
                {
                    cred.NetworkStatus = "NotTested";
                    cred.AuthenticationStatus = "NotTested";
                    await SaveToDatabaseAsync(cred);
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Candidate,
                        HttpStatusCode = HttpStatusCode.OK,
                        Detail = "SSH Private Key Header Detected."
                    };
                }

                // 3. DNS Resolution + SSRF Protection: resolve ONCE, validate ALL IPs, pin result
                var validatedIp = await ResolveAndValidateHostAsync(cred.Host);

                if (validatedIp is null)
                {
                    cred.NetworkStatus = "Restricted";
                    cred.AuthenticationStatus = "NotTested";
                    await SaveToDatabaseAsync(cred);
                    return ValidationResult.HasHttpError(
                        HttpStatusCode.Forbidden,
                        $"Target host {cred.Host} is restricted or could not be safely resolved.");
                }

                // All subsequent network operations use the validated IP — no further DNS lookups
                var targetAddress = validatedIp.ToString();

                // 4. Perform Network Connectivity Check (pinned to validated IP)
                var netResult = await _networkVerifier.VerifyConnectivityAsync(targetAddress, cred.Port);
                cred.NetworkStatus = netResult.IsAccessible ? "Accessible" : netResult.Status;

                if (!netResult.IsAccessible)
                {
                    await SaveToDatabaseAsync(cred);
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = HttpStatusCode.ServiceUnavailable,
                        Detail = $"Host {cred.Host}:{cred.Port} unreachable ({netResult.Status})"
                    };
                }

                // 5. Extract Banner (pinned to validated IP) and SSL Certificate (IP for TCP, hostname for SNI)
                var banner = await _networkVerifier.ExtractBannerAsync(targetAddress, cred.Port);
                var sslInfo = SslCertificateInfo.NotAvailable();
                if (cred.Port is 443 or 8443 or 2083 or 2087 or 993 or 995 or 465)
                {
                    // TCP destination: validated IP | TLS SNI + cert validation: original hostname
                    sslInfo = await _networkVerifier.ExtractSslCertificateAsync(targetAddress, cred.Port, cred.Host);
                }

                var metaObj = new Dictionary<string, object>
                {
                    ["banner"] = banner,
                    ["sslSubject"] = sslInfo.Subject,
                    ["sslIssuer"] = sslInfo.Issuer,
                    ["sslThumbprint"] = sslInfo.Thumbprint
                };

                cred.ServerMetadata = JsonSerializer.Serialize(metaObj);

                // 6. Safe Authentication Check (pinned to validated IP)
                if (!string.IsNullOrWhiteSpace(rawPassword))
                {
                    var authRes = await PerformAuthCheckAsync(cred, rawPassword, targetAddress);
                    cred.AuthenticationStatus = authRes.Status;
                }
                else
                {
                    cred.AuthenticationStatus = "NotTested";
                }

                // 7. OSINT & Geolocation Enrichment (executed when authenticated or accessible)
                if (cred.AuthenticationStatus == "Valid" || cred.NetworkStatus == "Accessible")
                {
                    try
                    {
                        cred.IsHoneypot = await _osintService.IsHoneypotAsync(cred.Host);
                        var greyResult = await _osintService.QueryGreyNoiseAsync(cred.Host);
                        cred.OSINTData = JsonSerializer.Serialize(new
                        {
                            greyNoiseClassification = greyResult.Classification,
                            greyNoiseIsBot = greyResult.IsBot
                        });
                    }
                    catch
                    {
                        cred.OSINTData = "{}";
                    }

                    try
                    {
                        var geoResult = await _geolocationService.GeolocateAsync(cred.Host);
                        cred.GeolocationData = JsonSerializer.Serialize(geoResult);
                    }
                    catch
                    {
                        cred.GeolocationData = "{}";
                    }
                }

                // 8. Persist ServerCredential record to database
                await SaveToDatabaseAsync(cred);

                // 9. Structured Result Classification
                return cred.AuthenticationStatus switch
                {
                    "Valid" => ValidationResult.Success(
                        HttpStatusCode.OK,
                        $"Discovered & Verified {cred.CredentialType} - Host: {cred.Host}:{cred.Port} - Auth: Valid"),

                    "Invalid" => ValidationResult.IsUnauthorized(
                        HttpStatusCode.Unauthorized,
                        $"Credential rejected for {cred.Host}:{cred.Port}."),

                    "NotTested" => new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Candidate,
                        HttpStatusCode = HttpStatusCode.OK,
                        Detail = $"Credential discovered at {cred.Host}:{cred.Port} but authentication was not tested."
                    },

                    _ => new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = HttpStatusCode.ServiceUnavailable,
                        Detail = $"Credential discovered at {cred.Host}:{cred.Port} but authentication could not be conclusively verified: {cred.AuthenticationStatus}"
                    }
                };
            }
            catch (Exception ex)
            {
                // Never log raw secret or sensitive credential material in logs or response error messages
                _logger?.LogError(ex, "Failed to validate server credential for provider {ProviderName}", ProviderName);
                return ValidationResult.HasProviderSpecificError("Server credential validation failed.");
            }
        }

        private async Task<AuthVerificationResult> PerformAuthCheckAsync(ServerCredential cred, string rawPassword, string targetAddress)
        {
            if (Enum.TryParse<CredentialType>(cred.CredentialType, out var type))
            {
                // TCP-based protocols: connect to validated IP directly
                // HTTP-based protocols: connect to validated IP, use original hostname for SNI/Host header
                return type switch
                {
                    CredentialType.SSH or CredentialType.SFTP => await _authVerifier.VerifySSHAsync(targetAddress, cred.Port, cred.Username, rawPassword),
                    CredentialType.FTP or CredentialType.FTPS => await _authVerifier.VerifyFTPAsync(targetAddress, cred.Port, cred.Username, rawPassword),
                    CredentialType.RDP => await _authVerifier.VerifyRDPAsync(targetAddress, cred.Port, cred.Username, rawPassword, cred.Domain),
                    CredentialType.SMTP or CredentialType.SMTP_Submission or CredentialType.SMTPS => await _authVerifier.VerifySMTPAsync(targetAddress, cred.Port, cred.Username, rawPassword),
                    CredentialType.IMAP or CredentialType.IMAPS => await _authVerifier.VerifyIMAPAsync(targetAddress, cred.Port, cred.Username, rawPassword),
                    CredentialType.POP3 or CredentialType.POP3S => await _authVerifier.VerifyPOP3Async(targetAddress, cred.Port, cred.Username, rawPassword),
                    CredentialType.cPanel_HTTP or CredentialType.cPanel_HTTPS => await _authVerifier.VerifyCPanelAsync(targetAddress, cred.Port, cred.Username, rawPassword, cred.Host),
                    CredentialType.WHM_HTTP or CredentialType.WHM_HTTPS => await _authVerifier.VerifyWHMAsync(targetAddress, cred.Port, cred.Username, rawPassword, cred.Host),
                    CredentialType.Plesk => await _authVerifier.VerifyPleskAsync(targetAddress, cred.Port, cred.Username, rawPassword, cred.Host),
                    _ => await _authVerifier.VerifyDatabaseAsync(type, targetAddress, cred.Port, cred.Username, rawPassword, "master")
                };
            }
            return AuthVerificationResult.Error("Unsupported protocol");
        }

        private async Task SaveToDatabaseAsync(ServerCredential cred)
        {
            try
            {
                using var db = new DBContext();
                var existing = await db.ServerCredentials
                    .FirstOrDefaultAsync(s => s.Host == cred.Host 
                                              && s.Port == cred.Port 
                                              && s.Username == cred.Username 
                                              && s.CredentialType == cred.CredentialType);

                if (existing != null)
                {
                    existing.NetworkStatus = cred.NetworkStatus;
                    existing.AuthenticationStatus = cred.AuthenticationStatus;
                    existing.Password = cred.Password;
                    existing.ServerMetadata = cred.ServerMetadata;
                    existing.GeolocationData = cred.GeolocationData;
                    existing.OSINTData = cred.OSINTData;
                    existing.RiskLevel = cred.RiskLevel;
                    existing.IsHoneypot = cred.IsHoneypot;
                    existing.LastVerifiedAt = DateTime.UtcNow;
                    existing.EntropyScore = cred.EntropyScore;
                    existing.SurroundingContext = cred.SurroundingContext;
                    db.ServerCredentials.Update(existing);
                }
                else
                {
                    cred.LastVerifiedAt = DateTime.UtcNow;
                    await db.ServerCredentials.AddAsync(cred);
                }
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to persist ServerCredential record to database");
            }
        }

        private (ServerCredential? cred, string rawPassword) ParseCredentialAndGetRawPassword(string matchText)
        {
            var cred = new ServerCredential
            {
                DiscoveredAt = DateTime.UtcNow,
                AuthenticationStatus = "NotTested",
                NetworkStatus = "NotTested"
            };

            var rawPassword = string.Empty;

            // Primary URI-based Parsing via System.Uri
            if (Uri.TryCreate(matchText, UriKind.Absolute, out var uriResult))
            {
                var scheme = uriResult.Scheme.ToLowerInvariant();
                if (scheme is "ftp" or "sftp" or "ftps" or "mysql" or "postgresql" or "postgres" or "mongodb" or "redis" or "smtp" or "rdp" or "cpanel" or "whm" or "plesk")
                {
                    cred.Host = uriResult.Host;

                    var userInfo = uriResult.UserInfo;
                    if (!string.IsNullOrEmpty(userInfo))
                    {
                        var colonIdx = userInfo.IndexOf(':');
                        if (colonIdx >= 0)
                        {
                            cred.Username = Uri.UnescapeDataString(userInfo[..colonIdx]);
                            rawPassword = Uri.UnescapeDataString(userInfo[(colonIdx + 1)..]);
                        }
                        else
                        {
                            cred.Username = Uri.UnescapeDataString(userInfo);
                        }
                    }

                    cred.Password = rawPassword;
                    cred.SurroundingContext = RedactPasswordInContext(matchText, rawPassword);
                    cred.EntropyScore = _entropyAnalyzer.CalculateEntropy(rawPassword);

                    switch (scheme)
                    {
                        case "sftp":
                            cred.CredentialType = "SFTP";
                            cred.Port = uriResult.Port > 0 ? uriResult.Port : 22;
                            cred.RiskLevel = "High";
                            return (cred, rawPassword);

                        case "ftps":
                            cred.CredentialType = "FTPS";
                            cred.Port = uriResult.Port > 0 ? uriResult.Port : 990;
                            cred.RiskLevel = "High";
                            return (cred, rawPassword);

                        case "ftp":
                            cred.CredentialType = "FTP";
                            cred.Port = uriResult.Port > 0 ? uriResult.Port : 21;
                            cred.RiskLevel = "High";
                            return (cred, rawPassword);

                        case "mysql":
                            cred.CredentialType = "MySQL";
                            cred.Port = uriResult.Port > 0 ? uriResult.Port : 3306;
                            cred.RiskLevel = "High";
                            var mysqlDb = uriResult.AbsolutePath.TrimStart('/');
                            if (!string.IsNullOrEmpty(mysqlDb)) cred.ServerMetadata = JsonSerializer.Serialize(new { database = mysqlDb });
                            return (cred, rawPassword);

                        case "postgresql" or "postgres":
                            cred.CredentialType = "PostgreSQL";
                            cred.Port = uriResult.Port > 0 ? uriResult.Port : 5432;
                            cred.RiskLevel = "High";
                            var pgDb = uriResult.AbsolutePath.TrimStart('/');
                            if (!string.IsNullOrEmpty(pgDb)) cred.ServerMetadata = JsonSerializer.Serialize(new { database = pgDb });
                            return (cred, rawPassword);

                        case "mongodb":
                            cred.CredentialType = "MongoDB";
                            cred.Port = uriResult.Port > 0 ? uriResult.Port : 27017;
                            cred.RiskLevel = "High";
                            var mongoDb = uriResult.AbsolutePath.TrimStart('/');
                            if (!string.IsNullOrEmpty(mongoDb)) cred.ServerMetadata = JsonSerializer.Serialize(new { database = mongoDb });
                            return (cred, rawPassword);

                        case "redis":
                            cred.CredentialType = "Redis";
                            cred.Port = uriResult.Port > 0 ? uriResult.Port : 6379;
                            cred.RiskLevel = "Medium";
                            return (cred, rawPassword);

                        case "smtp":
                            cred.CredentialType = "SMTP";
                            cred.Port = uriResult.Port > 0 ? uriResult.Port : 25;
                            cred.RiskLevel = "Medium";
                            return (cred, rawPassword);

                        case "rdp":
                            cred.CredentialType = "RDP";
                            cred.Port = uriResult.Port > 0 ? uriResult.Port : 3389;
                            cred.RiskLevel = "High";
                            return (cred, rawPassword);

                        case "cpanel":
                            cred.CredentialType = uriResult.Port == 2082 ? "cPanel_HTTP" : "cPanel_HTTPS";
                            cred.Port = uriResult.Port > 0 ? uriResult.Port : 2083;
                            cred.RiskLevel = "Critical";
                            return (cred, rawPassword);

                        case "whm":
                            cred.CredentialType = uriResult.Port == 2086 ? "WHM_HTTP" : "WHM_HTTPS";
                            cred.Port = uriResult.Port > 0 ? uriResult.Port : 2087;
                            cred.RiskLevel = "Critical";
                            return (cred, rawPassword);

                        case "plesk":
                            cred.CredentialType = "Plesk";
                            cred.Port = uriResult.Port > 0 ? uriResult.Port : 8443;
                            cred.RiskLevel = "Critical";
                            return (cred, rawPassword);
                    }
                }
            }

            // Fallback Parsing for Non-Standard URI formats (e.g. ADO.NET Connection Strings, SSH commands)
            var connStrMatch = Regex.Match(matchText, @"Server=([a-zA-Z0-9.-]+);Database=([a-zA-Z0-9_-]+);User Id=([a-zA-Z0-9._%+\\-]+);Password=([^;]+);", RegexOptions.IgnoreCase);
            if (connStrMatch.Success)
            {
                cred.CredentialType = "MSSQL";
                cred.Host = connStrMatch.Groups[1].Value;
                cred.Username = connStrMatch.Groups[3].Value;
                rawPassword = connStrMatch.Groups[4].Value;
                cred.Password = rawPassword;
                cred.Port = 1433;
                cred.RiskLevel = "High";
                cred.SurroundingContext = RedactPasswordInContext(matchText, rawPassword);
                cred.ServerMetadata = JsonSerializer.Serialize(new { database = connStrMatch.Groups[2].Value });
                cred.EntropyScore = _entropyAnalyzer.CalculateEntropy(rawPassword);
                return (cred, rawPassword);
            }

            var sshMatch = Regex.Match(matchText, @"ssh\s+([a-zA-Z0-9._%+\\-]+)@([a-zA-Z0-9.-]+|\[[a-fA-F0-9:]+\])", RegexOptions.IgnoreCase);
            if (sshMatch.Success)
            {
                cred.CredentialType = "SSH";
                cred.Username = sshMatch.Groups[1].Value;
                cred.Host = sshMatch.Groups[2].Value;
                cred.Port = 22;
                cred.RiskLevel = "Critical";
                rawPassword = _contextExtractor.FindRelatedPassword(matchText, cred.Username);
                cred.Password = rawPassword;
                cred.SurroundingContext = RedactPasswordInContext(matchText, rawPassword);
                cred.EntropyScore = _entropyAnalyzer.CalculateEntropy(rawPassword);

                return (cred, rawPassword);
            }

            if (Regex.IsMatch(matchText, @"-----BEGIN\s+.*PRIVATE\s+KEY-----", RegexOptions.IgnoreCase))
            {
                cred.CredentialType = "SSH";
                cred.Username = "NotExtracted";
                cred.Host = "HeaderDetectedOnly";
                cred.Port = 22;
                cred.RiskLevel = "Critical";
                cred.AuthenticationStatus = "NotTested";
                cred.SurroundingContext = matchText;
                rawPassword = string.Empty;
                var keyFingerprint = ComputeSha256(matchText);
                cred.Password = "SSH_PRIVATE_KEY_HEADER";
                cred.ServerMetadata = JsonSerializer.Serialize(new { keyFingerprint = keyFingerprint });
                return (cred, rawPassword);
            }

            return (null, string.Empty);
        }

        private static string RedactPasswordInContext(string context, string rawPassword)
        {
            if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(rawPassword)) return context;
            return context.Replace(rawPassword, "********");
        }

        /// <summary>
        /// Resolves the hostname ONCE via _dnsResolver and validates ALL returned IPs against SSRF restrictions.
        /// Returns the first validated public IP, or null if restricted/unresolvable.
        /// The returned IP should be used for ALL subsequent network operations (no further DNS lookups).
        /// </summary>
        internal async Task<IPAddress?> ResolveAndValidateHostAsync(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;
            
            // Clean bracketed IPv6 host string if present
            var cleanHost = host.Trim('[', ']');

            if (cleanHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                cleanHost.Equals("127.0.0.1") ||
                cleanHost.Equals("0.0.0.0") ||
                cleanHost.Equals("::1"))
            {
                return null;
            }

            try
            {
                // Resolve host to IP addresses — this is the ONLY DNS resolution that occurs
                IPAddress[] ips;
                if (IPAddress.TryParse(cleanHost, out var directIp))
                {
                    ips = [directIp];
                }
                else
                {
                    ips = await _dnsResolver.ResolveAsync(cleanHost);
                }

                if (ips.Length == 0) return null;

                // Validate ALL resolved IPs — reject if ANY is restricted
                foreach (var ip in ips)
                {
                    if (IsRestrictedIp(ip)) return null;
                }

                // All IPs validated — return the first one for pinned connections
                var selectedIp = ips[0];
                if (selectedIp.IsIPv4MappedToIPv6)
                {
                    selectedIp = selectedIp.MapToIPv4();
                }
                return selectedIp;
            }
            catch
            {
                // If DNS resolution fails, block connection safely
                return null;
            }
        }

        /// <summary>
        /// Checks whether an individual IP address falls within restricted ranges
        /// (loopback, private, link-local, cloud metadata, unique local).
        /// </summary>
        internal static bool IsRestrictedIp(IPAddress ip)
        {
            // Normalize IPv4-mapped IPv6 addresses before checking
            // (e.g. ::ffff:192.168.1.1 → 192.168.1.1)
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (IPAddress.IsLoopback(ip)) return true;

            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4) // IPv4 Private & Link-Local ranges
            {
                if (bytes[0] == 127 || // Loopback 127.0.0.0/8
                    bytes[0] == 10 ||  // Private 10.0.0.0/8
                    (bytes[0] == 169 && bytes[1] == 254) || // Link-Local / Cloud Metadata 169.254.0.0/16
                    (bytes[0] == 192 && bytes[1] == 168) || // Private 192.168.0.0/16
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)) // Private 172.16.0.0/12
                {
                    return true;
                }
            }
            else if (bytes.Length == 16) // IPv6 Loopback, Link-Local & Unique Local
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.Equals(IPAddress.IPv6Loopback))
                    return true;

                // fc00::/7 Unique Local Addresses
                if ((bytes[0] & 0xfe) == 0xfc)
                    return true;
            }

            return false;
        }

        private static string ComputeSha256(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw)));
        }
    }
}
