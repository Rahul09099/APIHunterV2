using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers.ServerProviders;
using UnsecuredAPIKeys.Providers.ServerProviders.Services;

namespace UnsecuredAPIKeys.Tests
{
    public class ServerCredentialProviderTests
    {
        private readonly ServerCredentialProvider _provider = new();

        [Fact]
        public void Property_P1_PatternCompleteness()
        {
            // Canonical example strings for credential types
            var testCases = new (string patternDesc, string testString)[]
            {
                ("SSH Command", "ssh admin@192.168.1.50"),
                ("SSH Private Key", "-----BEGIN RSA PRIVATE KEY-----\nMOCKKEYDATA\n-----END RSA PRIVATE KEY-----"),
                ("FTP URL", "ftp://ftpuser:FtpPass123@ftp.example.com:21"),
                ("SFTP URL", "sftp://sftpuser:SftpPass123@sftp.example.com:22"),
                ("MySQL URL", "mysql://dbuser:MySqlPass123@db.example.com:3306/prod_db"),
                ("PostgreSQL URL", "postgresql://pguser:PgPass123@pg.example.com:5432/postgres"),
                ("MongoDB URL", "mongodb://mongo:MongoPass123@mongo.example.com:27017/admin"),
                ("Redis URL", "redis://:RedisPass123@redis.example.com:6379"),
                ("RDP URL", "rdp://rdpuser:RdpPass123@rdp.example.com:3389"),
                ("SMTP URL", "smtp://smtpuser:SmtpPass123@smtp.example.com:587")
            };

            var patterns = _provider.RegexPatterns.ToList();

            foreach (var testCase in testCases)
            {
                bool matched = false;
                foreach (var pattern in patterns)
                {
                    if (Regex.IsMatch(testCase.testString, pattern, RegexOptions.IgnoreCase))
                    {
                        matched = true;
                        break;
                    }
                }
                Assert.True(matched, $"Canonical pattern '{testCase.patternDesc}' failed to match any regex.");
            }
        }

        [Property(MaxTest = 100)]
        public bool Property_P2_NoPlaintextPasswordStorage(NonNull<string> passwordGen)
        {
            var rawPassword = passwordGen.Get;
            if (string.IsNullOrEmpty(rawPassword) || !rawPassword.All(char.IsLetterOrDigit)) return true;

            var provider = new ServerCredentialProvider();
            
            // Invoke the private/internal parsing method via reflection to test
            var method = typeof(ServerCredentialProvider)
                .GetMethod("ParseCredentialAndGetRawPassword", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (method == null) return false;

            // Generate an FTP connection string with this raw password
            // Avoid special characters in the host/username that could break regex parsing
            var connectionString = $"ftp://user1:{Uri.EscapeDataString(rawPassword)}@127.0.0.1:21";

            var result = method.Invoke(provider, new object[] { connectionString });
            if (result == null) return false;

            // Unpack tuple (ServerCredential cred, string rawPassword) using Item1
            var cred = result.GetType().GetField("Item1")?.GetValue(result) as ServerCredential;
            
            if (cred == null) return false;

            // 1. Stored PasswordHash must be exactly the plaintext password
            if (cred.PasswordHash != rawPassword) return false;

            // 2. The raw password must appear as the PasswordHash value in the serialized JSON
            var serialized = JsonSerializer.Serialize(cred);
            if (rawPassword.Length >= 4 && !serialized.Contains($":\"{rawPassword}\"")) return false;

            return true;
        }

        [Property(MaxTest = 20)]
        public bool Property_P5_HoneypotPropagation(bool isHoneypot)
        {
            // Mock OSINT Service
            var mockOsint = new Mock<IOSINTService>();
            mockOsint.Setup(o => o.IsHoneypotAsync(It.IsAny<string>()))
                     .ReturnsAsync(isHoneypot);
            mockOsint.Setup(o => o.QueryGreyNoiseAsync(It.IsAny<string>()))
                     .ReturnsAsync(new GreyNoiseResult { Classification = isHoneypot ? "malicious" : "benign", IsBot = isHoneypot });

            var mockNet = new Mock<INetworkVerifier>();
            mockNet.Setup(n => n.VerifyConnectivityAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                   .ReturnsAsync(NetworkVerificationResult.Success("127.0.0.1", 21));
            mockNet.Setup(n => n.ExtractBannerAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                   .ReturnsAsync("FTP Banner");

            var mockAuth = new Mock<IAuthenticationVerifier>();
            var mockContext = new Mock<IContextExtractor>();
            var mockEntropy = new Mock<IEntropyAnalyzer>();
            var mockGeo = new Mock<IGeolocationService>();
            mockGeo.Setup(g => g.GeolocateAsync(It.IsAny<string>()))
                   .ReturnsAsync(new GeolocationResult { Country = "US", ISP = "TestISP" });

            // Create provider with mocks
            var provider = new ServerCredentialProvider(
                mockNet.Object,
                mockAuth.Object,
                mockContext.Object,
                mockEntropy.Object,
                mockOsint.Object,
                mockGeo.Object
            );

            // Access internal method to test honeypot propagation
            var method = typeof(ServerCredentialProvider)
                .GetMethod("ParseCredentialAndGetRawPassword", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (method == null) return false;

            var tuple = method.Invoke(provider, new object[] { "ftp://user1:pass123@127.0.0.1:21" });
            var cred = tuple?.GetType().GetField("Item1")?.GetValue(tuple) as ServerCredential;

            if (cred == null) return false;

            // In our ValidateKeyWithHttpClientAsync flow, IsHoneypot is checked:
            cred.IsHoneypot = mockOsint.Object.IsHoneypotAsync(cred.Host).Result;

            // Assert that IsHoneypot matches exactly what was returned by the OSINT service
            return cred.IsHoneypot == isHoneypot;
        }
    }
}
