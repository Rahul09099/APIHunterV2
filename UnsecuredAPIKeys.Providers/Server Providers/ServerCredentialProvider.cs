using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
            @"-----BEGIN\s+(RSA|DSA|EC|OPENSSH)\s+PRIVATE\s+KEY-----",
            @"Host\s+([a-zA-Z0-9.-]+)\s+User\s+([a-zA-Z0-9_-]+)",
            
            // FTP/SFTP Credentials
            @"(ftp|sftp|ftps)://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?",
            @"FTP_HOST\s*=\s*['""]?([a-zA-Z0-9.-]+)['""]?",
            @"FTP_USER\s*=\s*['""]?([a-zA-Z0-9_-]+)['""]?",
            @"FTP_PASS\s*=\s*['""]?([^\s'""\n]+)['""]?",
            
            // Database Connection Strings
            @"mysql://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?/([a-zA-Z0-9_-]+)",
            @"postgresql://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?/([a-zA-Z0-9_-]+)",
            @"mongodb://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?/([a-zA-Z0-9_-]+)",
            @"redis://(?::([^@\s]+)@)?([a-zA-Z0-9.-]+)(?::(\d+))?",
            @"Server=([a-zA-Z0-9.-]+);Database=([a-zA-Z0-9_-]+);User Id=([a-zA-Z0-9_-]+);Password=([^;]+);",
            @"jdbc:(mysql|postgresql|sqlserver)://([a-zA-Z0-9.-]+)(?::(\d+))?/([a-zA-Z0-9_-]+)",
            
            // RDP and Remote Access
            @"rdp://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?",
            @"mstsc\s+/v:([a-zA-Z0-9.-]+)(?::(\d+))?",
            @"vnc://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?",
            @"TeamViewer\s+ID:\s*(\d+)\s+Password:\s*([a-zA-Z0-9]+)",
            @"WinRM\s+([a-zA-Z0-9.-]+)\s+([a-zA-Z0-9_-]+)\s+([^\s]+)",
            
            // SMTP and Email Servers
            @"smtp://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?",
            @"SMTP_HOST\s*=\s*['""]?([a-zA-Z0-9.-]+)['""]?",
            @"SMTP_USER\s*=\s*['""]?([a-zA-Z0-9_@.-]+)['""]?",
            @"SMTP_PASSWORD\s*=\s*['""]?([^\s'""\n]+)['""]?",
            @"SMTP_PORT\s*=\s*['""]?(\d+)['""]?",
            @"imap://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?",
            @"pop3://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?",
            
            // cPanel and Control Panels
            @"CPANEL_USER\s*=\s*['""]?([a-zA-Z0-9_-]+)['""]?",
            @"CPANEL_PASS\s*=\s*['""]?([^\s'""\n]+)['""]?",
            @"WHM_USER\s*=\s*['""]?([a-zA-Z0-9_-]+)['""]?",
            @"WHM_PASS\s*=\s*['""]?([^\s'""\n]+)['""]?",
            @"PLESK_USER\s*=\s*['""]?([a-zA-Z0-9_-]+)['""]?",
            @"PLESK_PASS\s*=\s*['""]?([^\s'""\n]+)['""]?",
            
            // Cloud and Container
            @"KUBERNETES_SERVICE_HOST\s*=\s*['""]?([a-zA-Z0-9.-]+)['""]?",
            @"DOCKER_HOST\s*=\s*tcp://([a-zA-Z0-9.-]+):(\d+)",
            @"kubeconfig",
            
            // Web Server Authentication
            @"AuthUserFile\s+([^\s]+)",
            @"htpasswd\s+([^\s]+)",
            @"<user\s+username=['""]([^'""\s]+)['""].*password=['""]([^'""\s]+)['""]",
        ];

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey);
        }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // 1. Parse matched credential details (and isolate raw password from DB model)
                var (cred, rawPassword) = ParseCredentialAndGetRawPassword(apiKey);

                // 2. Perform Network Connectivity Check
                var netResult = await _networkVerifier.VerifyConnectivityAsync(cred.Host, cred.Port);
                cred.NetworkStatus = netResult.IsAccessible ? "Accessible" : netResult.Status;

                if (netResult.IsAccessible)
                {
                    // 3. Extract Banner and SSL Certificate
                    var banner = await _networkVerifier.ExtractBannerAsync(cred.Host, cred.Port);
                    var sslInfo = SslCertificateInfo.NotAvailable();
                    if (cred.Port == 443 || cred.Port == 8443 || cred.Port == 2083 || cred.Port == 2087 || cred.Port == 993 || cred.Port == 995 || cred.Port == 465)
                    {
                        sslInfo = await _networkVerifier.ExtractSslCertificateAsync(cred.Host, cred.Port);
                    }

                    cred.ServerMetadata = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        banner,
                        sslSubject = sslInfo.Subject,
                        sslIssuer = sslInfo.Issuer,
                        sslThumbprint = sslInfo.Thumbprint
                    });

                    // 4. Safe Auth verification attempt
                    var authRes = await PerformAuthCheckAsync(cred, rawPassword);
                    cred.AuthenticationStatus = authRes.Status;

                    // 5. Query OSINT for GreyNoise honeypot Classification
                    try
                    {
                        cred.IsHoneypot = await _osintService.IsHoneypotAsync(cred.Host);
                        var greyResult = await _osintService.QueryGreyNoiseAsync(cred.Host);
                        cred.OSINTData = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            greyNoiseClassification = greyResult.Classification,
                            greyNoiseIsBot = greyResult.IsBot
                        });
                    }
                    catch
                    {
                        cred.OSINTData = "{}";
                    }

                    // 6. Geolocate IP origin and cloud check
                    try
                    {
                        var geoResult = await _geolocationService.GeolocateAsync(cred.Host);
                        cred.GeolocationData = System.Text.Json.JsonSerializer.Serialize(geoResult);
                    }
                    catch
                    {
                        cred.GeolocationData = "{}";
                    }
                }

                // 7. Persist ServerCredential record to database
                await SaveToDatabaseAsync(cred);

                // Return Success with status
                return ValidationResult.Success(
                    HttpStatusCode.OK,
                    $"Discovered {cred.CredentialType} - Host: {cred.Host}:{cred.Port} - Auth: {cred.AuthenticationStatus}");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to validate server credential: {Match}", apiKey);
                return ValidationResult.HasProviderSpecificError(ex.Message);
            }
        }

        private async Task<AuthVerificationResult> PerformAuthCheckAsync(ServerCredential cred, string rawPassword)
        {
            if (Enum.TryParse<CredentialType>(cred.CredentialType, out var type))
            {
                return type switch
                {
                    CredentialType.SSH or CredentialType.SFTP => await _authVerifier.VerifySSHAsync(cred.Host, cred.Port, cred.Username, rawPassword),
                    CredentialType.FTP => await _authVerifier.VerifyFTPAsync(cred.Host, cred.Port, cred.Username, rawPassword),
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

        private (ServerCredential cred, string rawPassword) ParseCredentialAndGetRawPassword(string matchText)
        {
            var cred = new ServerCredential();
            var rawPassword = string.Empty;
            cred.DiscoveredAt = DateTime.UtcNow;

            // 1. FTP/SFTP: (ftp|sftp|ftps)://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?
            var ftpMatch = Regex.Match(matchText, @"(ftp|sftp|ftps)://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?", RegexOptions.IgnoreCase);
            if (ftpMatch.Success)
            {
                var proto = ftpMatch.Groups[1].Value.ToUpper();
                cred.CredentialType = proto == "SFTP" ? "SFTP" : "FTP";
                cred.Username = ftpMatch.Groups[2].Value;
                rawPassword = ftpMatch.Groups[3].Value;
                cred.PasswordHash = rawPassword;
                cred.Host = ftpMatch.Groups[4].Value;
                cred.Port = ftpMatch.Groups[5].Success ? int.Parse(ftpMatch.Groups[5].Value) : (proto == "SFTP" ? 22 : 21);
                cred.RiskLevel = "High";
                cred.SurroundingContext = matchText;
                cred.EntropyScore = _entropyAnalyzer.CalculateEntropy(rawPassword);
                return (cred, rawPassword);
            }

            // 2. MySQL: mysql://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?/([a-zA-Z0-9_-]+)
            var mysqlMatch = Regex.Match(matchText, @"mysql://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?/([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase);
            if (mysqlMatch.Success)
            {
                cred.CredentialType = "MySQL";
                cred.Username = mysqlMatch.Groups[1].Value;
                rawPassword = mysqlMatch.Groups[2].Value;
                cred.PasswordHash = rawPassword;
                cred.Host = mysqlMatch.Groups[3].Value;
                cred.Port = mysqlMatch.Groups[4].Success ? int.Parse(mysqlMatch.Groups[4].Value) : 3306;
                cred.RiskLevel = "High";
                cred.SurroundingContext = matchText;
                cred.EntropyScore = _entropyAnalyzer.CalculateEntropy(rawPassword);
                return (cred, rawPassword);
            }

            // 3. PostgreSQL: postgresql://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?/([a-zA-Z0-9_-]+)
            var pgMatch = Regex.Match(matchText, @"postgresql://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?/([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase);
            if (pgMatch.Success)
            {
                cred.CredentialType = "PostgreSQL";
                cred.Username = pgMatch.Groups[1].Value;
                rawPassword = pgMatch.Groups[2].Value;
                cred.PasswordHash = rawPassword;
                cred.Host = pgMatch.Groups[3].Value;
                cred.Port = pgMatch.Groups[4].Success ? int.Parse(pgMatch.Groups[4].Value) : 5432;
                cred.RiskLevel = "High";
                cred.SurroundingContext = matchText;
                cred.EntropyScore = _entropyAnalyzer.CalculateEntropy(rawPassword);
                return (cred, rawPassword);
            }

            // 4. MongoDB: mongodb://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?/([a-zA-Z0-9_-]+)
            var mongoMatch = Regex.Match(matchText, @"mongodb://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?/([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase);
            if (mongoMatch.Success)
            {
                cred.CredentialType = "MongoDB";
                cred.Username = mongoMatch.Groups[1].Value;
                rawPassword = mongoMatch.Groups[2].Value;
                cred.PasswordHash = rawPassword;
                cred.Host = mongoMatch.Groups[3].Value;
                cred.Port = mongoMatch.Groups[4].Success ? int.Parse(mongoMatch.Groups[4].Value) : 27017;
                cred.RiskLevel = "High";
                cred.SurroundingContext = matchText;
                cred.EntropyScore = _entropyAnalyzer.CalculateEntropy(rawPassword);
                return (cred, rawPassword);
            }

            // 5. Redis: redis://(?::([^@\s]+)@)?([a-zA-Z0-9.-]+)(?::(\d+))?
            var redisMatch = Regex.Match(matchText, @"redis://(?::([^@\s]+)@)?([a-zA-Z0-9.-]+)(?::(\d+))?", RegexOptions.IgnoreCase);
            if (redisMatch.Success)
            {
                cred.CredentialType = "Redis";
                rawPassword = redisMatch.Groups[1].Success ? redisMatch.Groups[1].Value : string.Empty;
                cred.PasswordHash = rawPassword;
                cred.Host = redisMatch.Groups[2].Value;
                cred.Port = redisMatch.Groups[3].Success ? int.Parse(redisMatch.Groups[3].Value) : 6379;
                cred.RiskLevel = "Medium";
                cred.SurroundingContext = matchText;
                cred.EntropyScore = _entropyAnalyzer.CalculateEntropy(rawPassword);
                return (cred, rawPassword);
            }

            // 6. SMTP: smtp://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?
            var smtpMatch = Regex.Match(matchText, @"smtp://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?", RegexOptions.IgnoreCase);
            if (smtpMatch.Success)
            {
                cred.CredentialType = "SMTP";
                cred.Username = smtpMatch.Groups[1].Value;
                rawPassword = smtpMatch.Groups[2].Value;
                cred.PasswordHash = rawPassword;
                cred.Host = smtpMatch.Groups[3].Value;
                cred.Port = smtpMatch.Groups[4].Success ? int.Parse(smtpMatch.Groups[4].Value) : 25;
                cred.RiskLevel = "Medium";
                cred.SurroundingContext = matchText;
                cred.EntropyScore = _entropyAnalyzer.CalculateEntropy(rawPassword);
                return (cred, rawPassword);
            }

            // 7. RDP: rdp://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?
            var rdpMatch = Regex.Match(matchText, @"rdp://([a-zA-Z0-9_-]+):([^@\s]+)@([a-zA-Z0-9.-]+)(?::(\d+))?", RegexOptions.IgnoreCase);
            if (rdpMatch.Success)
            {
                cred.CredentialType = "RDP";
                cred.Username = rdpMatch.Groups[1].Value;
                rawPassword = rdpMatch.Groups[2].Value;
                cred.PasswordHash = rawPassword;
                cred.Host = rdpMatch.Groups[3].Value;
                cred.Port = rdpMatch.Groups[4].Success ? int.Parse(rdpMatch.Groups[4].Value) : 3389;
                cred.RiskLevel = "High";
                cred.SurroundingContext = matchText;
                cred.EntropyScore = _entropyAnalyzer.CalculateEntropy(rawPassword);
                return (cred, rawPassword);
            }

            // 8. SSH: ssh\s+([a-zA-Z0-9_-]+)@([a-zA-Z0-9.-]+)
            var sshMatch = Regex.Match(matchText, @"ssh\s+([a-zA-Z0-9_-]+)@([a-zA-Z0-9.-]+)", RegexOptions.IgnoreCase);
            if (sshMatch.Success)
            {
                cred.CredentialType = "SSH";
                cred.Username = sshMatch.Groups[1].Value;
                cred.Host = sshMatch.Groups[2].Value;
                cred.Port = 22;
                cred.RiskLevel = "Critical";
                cred.SurroundingContext = matchText;
                // Search context for password
                rawPassword = _contextExtractor.FindRelatedPassword(matchText, cred.Username);
                cred.PasswordHash = rawPassword;
                cred.EntropyScore = _entropyAnalyzer.CalculateEntropy(rawPassword);
                return (cred, rawPassword);
            }

            // Private Key header: -----BEGIN\s+(RSA|DSA|EC|OPENSSH)\s+PRIVATE\s+KEY-----
            if (matchText.Contains("PRIVATE KEY"))
            {
                cred.CredentialType = "SSH";
                cred.Username = "root";
                cred.Host = "Unknown";
                cred.Port = 22;
                cred.RiskLevel = "Critical";
                cred.SurroundingContext = matchText;
                rawPassword = "KEY_AUTHENTICATED";
                cred.PasswordHash = rawPassword;
                return (cred, rawPassword);
            }

            // Fallback default
            cred.CredentialType = "SSH";
            cred.Username = "root";
            cred.Host = "127.0.0.1";
            cred.Port = 22;
            cred.RiskLevel = "Low";
            cred.SurroundingContext = matchText;
            return (cred, rawPassword);
        }

        private string ComputeSha256(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw)));
        }
    }
}
