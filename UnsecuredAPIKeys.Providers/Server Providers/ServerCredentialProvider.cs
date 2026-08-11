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
        private readonly ILogger<ServerCredentialProvider>? _logger;

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
            _logger = null;
        }

        // DI-enabled constructor
        public ServerCredentialProvider(
            INetworkVerifier networkVerifier,
            IAuthenticationVerifier authVerifier,
            IContextExtractor contextExtractor,
            IEntropyAnalyzer entropyAnalyzer,
            IOSINTService osintService,
            IGeolocationService geolocationService,
            ILogger<ServerCredentialProvider>? logger = null) : base(logger)
        {
            _networkVerifier = networkVerifier;
            _authVerifier = authVerifier;
            _contextExtractor = contextExtractor;
            _entropyAnalyzer = entropyAnalyzer;
            _osintService = osintService;
            _geolocationService = geolocationService;
            _logger = logger;
        }

        public override IEnumerable<string> RegexPatterns =>
        [
            // SSH Credentials
            @"ssh\s+([a-zA-Z0-9_-]+)@([a-zA-Z0-9.-]+)",
            @"-----BEGIN\s+.*PRIVATE\s+KEY-----",
            
            // FTP/SFTP/FTPS URIs
            @"(ftp|sftp|ftps)://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?",
            
            // Database Connection URIs & ADO.NET Strings
            @"mysql://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?(?:/([a-zA-Z0-9_-]+))?",
            @"postgresql://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?(?:/([a-zA-Z0-9_-]+))?",
            @"mongodb://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?(?:/([a-zA-Z0-9_-]+))?",
            @"redis://(?::([^@\s]+)@)?([a-zA-Z0-9.-]+)(?::(\d+))?",
            @"Server=([a-zA-Z0-9.-]+);Database=([a-zA-Z0-9_-]+);User Id=([a-zA-Z0-9_-]+);Password=([^;]+);",
            
            // RDP & Remote Access
            @"rdp://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?",
            
            // SMTP & Email Servers
            @"smtp://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?"
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
                    return ValidationResult.Success(HttpStatusCode.OK, "SSH Private Key Header Detected.");
                }

                // 3. DNS Resolution + Deep SSRF & Localhost/Private IP Protection
                if (await IsRestrictedOrLocalHostAsync(cred.Host))
                {
                    cred.NetworkStatus = "Restricted";
                    cred.AuthenticationStatus = "NotTested";
                    await SaveToDatabaseAsync(cred);
                    return ValidationResult.HasHttpError(HttpStatusCode.Forbidden, $"Target host {cred.Host} is restricted (Internal/SSRF Protection).");
                }

                // 4. Perform Network Connectivity Check
                var netResult = await _networkVerifier.VerifyConnectivityAsync(cred.Host, cred.Port);
                cred.NetworkStatus = netResult.IsAccessible ? "Accessible" : netResult.Status;

                if (!netResult.IsAccessible)
                {
                    await SaveToDatabaseAsync(cred);
                    return ValidationResult.HasHttpError(HttpStatusCode.ServiceUnavailable, $"Host {cred.Host}:{cred.Port} unreachable ({netResult.Status})");
                }

                // 5. Extract Banner and SSL Certificate
                var banner = await _networkVerifier.ExtractBannerAsync(cred.Host, cred.Port);
                var sslInfo = SslCertificateInfo.NotAvailable();
                if (cred.Port is 443 or 8443 or 2083 or 2087 or 993 or 995 or 465)
                {
                    sslInfo = await _networkVerifier.ExtractSslCertificateAsync(cred.Host, cred.Port);
                }

                var metaObj = new Dictionary<string, object>
                {
                    ["banner"] = banner,
                    ["sslSubject"] = sslInfo.Subject,
                    ["sslIssuer"] = sslInfo.Issuer,
                    ["sslThumbprint"] = sslInfo.Thumbprint
                };

                cred.ServerMetadata = JsonSerializer.Serialize(metaObj);

                // 6. Safe Authentication Check
                if (cred.AuthenticationStatus != "NotTested" && !string.IsNullOrWhiteSpace(rawPassword))
                {
                    var authRes = await PerformAuthCheckAsync(cred, rawPassword);
                    cred.AuthenticationStatus = authRes.Status;
                }
                else
                {
                    cred.AuthenticationStatus = "NotTested";
                }

                // 7. OSINT & Geolocation Enrichment (best-effort)
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

                // 8. Persist ServerCredential record to database
                await SaveToDatabaseAsync(cred);

                // 9. Result Classification
                if (cred.AuthenticationStatus == "Valid")
                {
                    return ValidationResult.Success(
                        HttpStatusCode.OK,
                        $"Discovered & Verified {cred.CredentialType} - Host: {cred.Host}:{cred.Port} - Auth: Valid");
                }

                return ValidationResult.IsUnauthorized(
                    HttpStatusCode.Unauthorized,
                    $"Discovered {cred.CredentialType} at {cred.Host}:{cred.Port} - Auth Status: {cred.AuthenticationStatus}");
            }
            catch (Exception ex)
            {
                // Never log raw secret or sensitive credential material in logs or response error messages
                _logger?.LogError(ex, "Failed to validate server credential for provider {ProviderName}", ProviderName);
                return ValidationResult.HasProviderSpecificError("Server credential validation failed.");
            }
        }

        private async Task<AuthVerificationResult> PerformAuthCheckAsync(ServerCredential cred, string rawPassword)
        {
            if (Enum.TryParse<CredentialType>(cred.CredentialType, out var type))
            {
                return type switch
                {
                    CredentialType.SSH or CredentialType.SFTP => await _authVerifier.VerifySSHAsync(cred.Host, cred.Port, cred.Username, rawPassword),
                    CredentialType.FTP or CredentialType.FTPS => await _authVerifier.VerifyFTPAsync(cred.Host, cred.Port, cred.Username, rawPassword),
                    CredentialType.RDP => await _authVerifier.VerifyRDPAsync(cred.Host, cred.Port, cred.Username, rawPassword, cred.Domain),
                    CredentialType.SMTP or CredentialType.SMTP_Submission or CredentialType.SMTPS => await _authVerifier.VerifySMTPAsync(cred.Host, cred.Port, cred.Username, rawPassword),
                    CredentialType.IMAP or CredentialType.IMAPS => await _authVerifier.VerifyIMAPAsync(cred.Host, cred.Port, cred.Username, rawPassword),
                    CredentialType.POP3 or CredentialType.POP3S => await _authVerifier.VerifyPOP3Async(cred.Host, cred.Port, cred.Username, rawPassword),
                    CredentialType.cPanel_HTTP or CredentialType.cPanel_HTTPS => await _authVerifier.VerifyCPanelAsync(cred.Host, cred.Port, cred.Username, rawPassword),
                    CredentialType.WHM_HTTP or CredentialType.WHM_HTTPS => await _authVerifier.VerifyWHMAsync(cred.Host, cred.Port, cred.Username, rawPassword),
                    CredentialType.Plesk => await _authVerifier.VerifyPleskAsync(cred.Host, cred.Port, cred.Username, rawPassword),
                    _ => await _authVerifier.VerifyDatabaseAsync(type, cred.Host, cred.Port, cred.Username, rawPassword, "master")
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
            var cred = new ServerCredential();
            var rawPassword = string.Empty;
            cred.DiscoveredAt = DateTime.UtcNow;

            // Primary URI-based Parsing via System.Uri
            if (Uri.TryCreate(matchText, UriKind.Absolute, out var uriResult))
            {
                var scheme = uriResult.Scheme.ToLowerInvariant();
                if (scheme is "ftp" or "sftp" or "ftps" or "mysql" or "postgresql" or "mongodb" or "redis" or "smtp" or "rdp")
                {
                    cred.Host = uriResult.Host;
                    cred.SurroundingContext = matchText;

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

                    cred.PasswordHash = rawPassword;
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

                        case "postgresql":
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
                    }
                }
            }

            // Fallback Parsing for Non-Standard URI formats (e.g. ADO.NET Connection Strings, SSH commands)
            var connStrMatch = Regex.Match(matchText, @"Server=([a-zA-Z0-9.-]+);Database=([a-zA-Z0-9_-]+);User Id=([a-zA-Z0-9_-]+);Password=([^;]+);", RegexOptions.IgnoreCase);
            if (connStrMatch.Success)
            {
                cred.CredentialType = "MSSQL";
                cred.Host = connStrMatch.Groups[1].Value;
                cred.Username = connStrMatch.Groups[3].Value;
                rawPassword = connStrMatch.Groups[4].Value;
                cred.PasswordHash = rawPassword;
                cred.Port = 1433;
                cred.RiskLevel = "High";
                cred.SurroundingContext = matchText;
                cred.ServerMetadata = JsonSerializer.Serialize(new { database = connStrMatch.Groups[2].Value });
                cred.EntropyScore = _entropyAnalyzer.CalculateEntropy(rawPassword);
                return (cred, rawPassword);
            }

            var sshMatch = Regex.Match(matchText, @"ssh\s+([a-zA-Z0-9_-]+)@([a-zA-Z0-9.-]+)", RegexOptions.IgnoreCase);
            if (sshMatch.Success)
            {
                cred.CredentialType = "SSH";
                cred.Username = sshMatch.Groups[1].Value;
                cred.Host = sshMatch.Groups[2].Value;
                cred.Port = 22;
                cred.RiskLevel = "Critical";
                cred.SurroundingContext = matchText;
                rawPassword = _contextExtractor.FindRelatedPassword(matchText, cred.Username);
                cred.PasswordHash = rawPassword;
                cred.EntropyScore = _entropyAnalyzer.CalculateEntropy(rawPassword);

                if (string.IsNullOrWhiteSpace(rawPassword))
                {
                    cred.AuthenticationStatus = "NotTested";
                }
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
                cred.PasswordHash = ComputeSha256(matchText);
                return (cred, rawPassword);
            }

            return (null, string.Empty);
        }

        private static async Task<bool> IsRestrictedOrLocalHostAsync(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return true;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("127.0.0.1") ||
                host.Equals("0.0.0.0") ||
                host.Equals("::1"))
            {
                return true;
            }

            try
            {
                // Resolve host to IP addresses (DNS resolution check to prevent DNS-rebinding SSRF)
                IPAddress[] ips;
                if (IPAddress.TryParse(host, out var directIp))
                {
                    ips = [directIp];
                }
                else
                {
                    ips = await Dns.GetHostAddressesAsync(host);
                }

                foreach (var ip in ips)
                {
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
                }
            }
            catch
            {
                // If DNS resolution fails, block connection safely
                return true;
            }

            return false;
        }

        private string ComputeSha256(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw)));
        }
    }
}
