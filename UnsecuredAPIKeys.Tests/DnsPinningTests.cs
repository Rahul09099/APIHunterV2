using System;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using Moq;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers.ServerProviders;
using UnsecuredAPIKeys.Providers.ServerProviders.Services;

namespace UnsecuredAPIKeys.Tests
{
    /// <summary>
    /// Behavioral tests that prove the DNS-pinning SSRF protection works correctly.
    /// Security invariant: the IP that passes SSRF validation must be the exact IP
    /// used for all subsequent network connections — no second DNS lookup occurs.
    /// </summary>
    public class DnsPinningTests
    {
        // ══════════════════════════════════════════════════════════════════════
        //  IsRestrictedIp — unit tests (internal, called directly)
        // ══════════════════════════════════════════════════════════════════════

        // ── Private IPv4 ranges ───────────────────────────────────────────────

        [Theory]
        [InlineData("10.0.0.1")]
        [InlineData("10.255.255.255")]
        [InlineData("192.168.1.1")]
        [InlineData("192.168.0.100")]
        [InlineData("172.16.0.1")]
        [InlineData("172.31.255.255")]
        public void IsRestrictedIp_PrivateIPv4_ReturnsTrue(string ip)
        {
            Assert.True(ServerCredentialProvider.IsRestrictedIp(IPAddress.Parse(ip)));
        }

        // ── Loopback ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("127.0.0.2")]
        [InlineData("127.255.255.255")]
        public void IsRestrictedIp_IPv4Loopback_ReturnsTrue(string ip)
        {
            Assert.True(ServerCredentialProvider.IsRestrictedIp(IPAddress.Parse(ip)));
        }

        [Fact]
        public void IsRestrictedIp_IPv6Loopback_ReturnsTrue()
        {
            Assert.True(ServerCredentialProvider.IsRestrictedIp(IPAddress.Parse("::1")));
        }

        // ── Cloud metadata / link-local ───────────────────────────────────────

        [Theory]
        [InlineData("169.254.169.254")] // AWS/GCP/Azure metadata
        [InlineData("169.254.0.1")]     // Generic link-local
        public void IsRestrictedIp_LinkLocal_ReturnsTrue(string ip)
        {
            Assert.True(ServerCredentialProvider.IsRestrictedIp(IPAddress.Parse(ip)));
        }

        // ── IPv6 restricted ranges ────────────────────────────────────────────

        [Theory]
        [InlineData("fc00::1")]  // Unique local
        [InlineData("fd00::1")]  // Unique local
        [InlineData("fe80::1")]  // Link-local
        public void IsRestrictedIp_IPv6Restricted_ReturnsTrue(string ip)
        {
            Assert.True(ServerCredentialProvider.IsRestrictedIp(IPAddress.Parse(ip)));
        }

        // ── IPv4-mapped IPv6 private addresses (NOT pre-converted) ────────────
        // These test that IsRestrictedIp handles IPv4-mapped IPv6 internally,
        // WITHOUT the caller needing to call .MapToIPv4() first.

        [Fact]
        public void IsRestrictedIp_IPv4MappedPrivate_WithoutPreConversion_ReturnsTrue()
        {
            // ::ffff:192.168.1.1 — still 16 bytes, NOT pre-normalized
            var ip = IPAddress.Parse("::ffff:192.168.1.1");
            Assert.Equal(16, ip.GetAddressBytes().Length); // confirm it's IPv6 format
            Assert.True(ServerCredentialProvider.IsRestrictedIp(ip));
        }

        [Fact]
        public void IsRestrictedIp_IPv4MappedLoopback_WithoutPreConversion_ReturnsTrue()
        {
            var ip = IPAddress.Parse("::ffff:127.0.0.1");
            Assert.Equal(16, ip.GetAddressBytes().Length);
            Assert.True(ServerCredentialProvider.IsRestrictedIp(ip));
        }

        [Fact]
        public void IsRestrictedIp_IPv4Mapped10Net_WithoutPreConversion_ReturnsTrue()
        {
            var ip = IPAddress.Parse("::ffff:10.0.0.5");
            Assert.Equal(16, ip.GetAddressBytes().Length);
            Assert.True(ServerCredentialProvider.IsRestrictedIp(ip));
        }

        [Fact]
        public void IsRestrictedIp_IPv4MappedMetadata_WithoutPreConversion_ReturnsTrue()
        {
            var ip = IPAddress.Parse("::ffff:169.254.169.254");
            Assert.Equal(16, ip.GetAddressBytes().Length);
            Assert.True(ServerCredentialProvider.IsRestrictedIp(ip));
        }

        // ── Public IPs ────────────────────────────────────────────────────────

        [Theory]
        [InlineData("8.8.8.8")]
        [InlineData("203.0.113.50")]
        [InlineData("1.1.1.1")]
        public void IsRestrictedIp_PublicIp_ReturnsFalse(string ip)
        {
            Assert.False(ServerCredentialProvider.IsRestrictedIp(IPAddress.Parse(ip)));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ResolveAndValidateHostAsync — with mocked DNS
        // ══════════════════════════════════════════════════════════════════════

        private static ServerCredentialProvider CreateProviderWithDns(Mock<IDnsResolver> mockDns)
        {
            return new ServerCredentialProvider(
                new Mock<INetworkVerifier>().Object,
                new Mock<IAuthenticationVerifier>().Object,
                new Mock<IContextExtractor>().Object,
                new Mock<IEntropyAnalyzer>().Object,
                new Mock<IOSINTService>().Object,
                new Mock<IGeolocationService>().Object,
                mockDns.Object);
        }

        // ── A. Mixed DNS response: public + private → REJECT ──────────────────

        [Fact]
        public async Task ResolveAndValidate_MixedPublicAndPrivate_ReturnsNull()
        {
            var mockDns = new Mock<IDnsResolver>();
            mockDns.Setup(d => d.ResolveAsync("evil.example.com"))
                   .ReturnsAsync(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("127.0.0.1") });

            var provider = CreateProviderWithDns(mockDns);
            var result = await provider.ResolveAndValidateHostAsync("evil.example.com");

            Assert.Null(result);
        }

        [Fact]
        public async Task ResolveAndValidate_MixedPublicAndPrivate10Net_ReturnsNull()
        {
            var mockDns = new Mock<IDnsResolver>();
            mockDns.Setup(d => d.ResolveAsync("attacker.test"))
                   .ReturnsAsync(new[] { IPAddress.Parse("203.0.113.50"), IPAddress.Parse("10.0.0.5") });

            var provider = CreateProviderWithDns(mockDns);
            var result = await provider.ResolveAndValidateHostAsync("attacker.test");

            Assert.Null(result);
        }

        [Fact]
        public async Task ResolveAndValidate_MixedPublicAndIPv4Mapped_ReturnsNull()
        {
            var mockDns = new Mock<IDnsResolver>();
            mockDns.Setup(d => d.ResolveAsync("sneaky.test"))
                   .ReturnsAsync(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("::ffff:192.168.1.1") });

            var provider = CreateProviderWithDns(mockDns);
            var result = await provider.ResolveAndValidateHostAsync("sneaky.test");

            Assert.Null(result);
        }

        // ── Public-only DNS → returns validated IP ────────────────────────────

        [Fact]
        public async Task ResolveAndValidate_AllPublic_ReturnsFirstIp()
        {
            var mockDns = new Mock<IDnsResolver>();
            mockDns.Setup(d => d.ResolveAsync("safe.example.com"))
                   .ReturnsAsync(new[] { IPAddress.Parse("203.0.113.10"), IPAddress.Parse("203.0.113.20") });

            var provider = CreateProviderWithDns(mockDns);
            var result = await provider.ResolveAndValidateHostAsync("safe.example.com");

            Assert.NotNull(result);
            Assert.Equal(IPAddress.Parse("203.0.113.10"), result);
        }

        // ── DNS resolution called exactly once ────────────────────────────────

        [Fact]
        public async Task ResolveAndValidate_CallsDnsExactlyOnce()
        {
            var mockDns = new Mock<IDnsResolver>();
            mockDns.Setup(d => d.ResolveAsync("example.com"))
                   .ReturnsAsync(new[] { IPAddress.Parse("93.184.216.34") });

            var provider = CreateProviderWithDns(mockDns);
            await provider.ResolveAndValidateHostAsync("example.com");

            mockDns.Verify(d => d.ResolveAsync("example.com"), Times.Once);
        }

        // ── IP literal skips DNS entirely ─────────────────────────────────────

        [Fact]
        public async Task ResolveAndValidate_IpLiteral_SkipsDns()
        {
            var mockDns = new Mock<IDnsResolver>();
            var provider = CreateProviderWithDns(mockDns);

            var result = await provider.ResolveAndValidateHostAsync("8.8.8.8");

            Assert.NotNull(result);
            Assert.Equal(IPAddress.Parse("8.8.8.8"), result);
            mockDns.Verify(d => d.ResolveAsync(It.IsAny<string>()), Times.Never);
        }

        // ── Edge cases ────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task ResolveAndValidate_NullOrEmpty_ReturnsNull(string? host)
        {
            var provider = CreateProviderWithDns(new Mock<IDnsResolver>());
            var result = await provider.ResolveAndValidateHostAsync(host!);
            Assert.Null(result);
        }

        [Fact]
        public async Task ResolveAndValidate_DnsFailure_ReturnsNull()
        {
            var mockDns = new Mock<IDnsResolver>();
            mockDns.Setup(d => d.ResolveAsync("broken.test"))
                   .ThrowsAsync(new System.Net.Sockets.SocketException());

            var provider = CreateProviderWithDns(mockDns);
            var result = await provider.ResolveAndValidateHostAsync("broken.test");

            Assert.Null(result);
        }

        [Fact]
        public async Task ResolveAndValidate_BracketedIPv6Loopback_ReturnsNull()
        {
            var provider = CreateProviderWithDns(new Mock<IDnsResolver>());
            var result = await provider.ResolveAndValidateHostAsync("[::1]");
            Assert.Null(result);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  B. Full-flow behavioral test: DNS pinning end-to-end
        //  Proves: hostname → DNS once → validated IP → all downstream calls use IP
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task FullFlow_HostnameResolved_AllDownstreamCallsUseValidatedIp()
        {
            // Arrange: DNS returns a single public IP for "db.example.com"
            var mockDns = new Mock<IDnsResolver>();
            mockDns.Setup(d => d.ResolveAsync("db.example.com"))
                   .ReturnsAsync(new[] { IPAddress.Parse("93.184.216.34") });

            string? capturedConnectivityHost = null;
            string? capturedBannerHost = null;
            string? capturedAuthHost = null;

            var mockNet = new Mock<INetworkVerifier>();
            mockNet.Setup(n => n.VerifyConnectivityAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                   .Callback<string, int, int>((host, _, _) => capturedConnectivityHost = host)
                   .ReturnsAsync(NetworkVerificationResult.Success("93.184.216.34", 21));
            mockNet.Setup(n => n.ExtractBannerAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                   .Callback<string, int, int>((host, _, _) => capturedBannerHost = host)
                   .ReturnsAsync("220 FTP Ready");

            var mockAuth = new Mock<IAuthenticationVerifier>();
            mockAuth.Setup(a => a.VerifyFTPAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
                    .Callback<string, int, string, string>((host, _, _, _) => capturedAuthHost = host)
                    .ReturnsAsync(AuthVerificationResult.Valid("FTP ok"));

            var mockContext = new Mock<IContextExtractor>();
            var mockEntropy = new Mock<IEntropyAnalyzer>();
            var mockOsint = new Mock<IOSINTService>();
            mockOsint.Setup(o => o.IsHoneypotAsync(It.IsAny<string>())).ReturnsAsync(false);
            mockOsint.Setup(o => o.QueryGreyNoiseAsync(It.IsAny<string>()))
                     .ReturnsAsync(new GreyNoiseResult { Classification = "benign", IsBot = false });
            var mockGeo = new Mock<IGeolocationService>();
            mockGeo.Setup(g => g.GeolocateAsync(It.IsAny<string>()))
                   .ReturnsAsync(new GeolocationResult { Country = "US", ISP = "TestISP" });

            var provider = new ServerCredentialProvider(
                mockNet.Object, mockAuth.Object, mockContext.Object,
                mockEntropy.Object, mockOsint.Object, mockGeo.Object,
                mockDns.Object);

            // Use a HOSTNAME (not IP literal) so DNS is actually invoked
            var input = "ftp://user1:pass123@db.example.com:21";

            var validateMethod = typeof(ServerCredentialProvider)
                .GetMethod("ValidateKeyWithHttpClientAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(validateMethod);

            using var httpClient = new System.Net.Http.HttpClient();
            var task = (Task)validateMethod!.Invoke(provider, new object[] { input, httpClient })!;
            await task;

            // Assert: DNS was called exactly once
            mockDns.Verify(d => d.ResolveAsync("db.example.com"), Times.Once);

            // Assert: connectivity check received the IP, not "db.example.com"
            Assert.Equal("93.184.216.34", capturedConnectivityHost);

            // Assert: banner extraction received the IP
            Assert.Equal("93.184.216.34", capturedBannerHost);

            // Assert: FTP auth received the IP
            Assert.Equal("93.184.216.34", capturedAuthHost);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  C. SSL pinning: TCP → validated IP, SNI → original hostname
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task FullFlow_SslExtraction_UsesIpForTcp_HostnameForSni()
        {
            var mockDns = new Mock<IDnsResolver>();
            mockDns.Setup(d => d.ResolveAsync("cp.example.com"))
                   .ReturnsAsync(new[] { IPAddress.Parse("203.0.113.50") });

            string? capturedSslTarget = null;
            string? capturedSslSni = null;

            var mockNet = new Mock<INetworkVerifier>();
            mockNet.Setup(n => n.VerifyConnectivityAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                   .ReturnsAsync(NetworkVerificationResult.Success("203.0.113.50", 2083));
            mockNet.Setup(n => n.ExtractBannerAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                   .ReturnsAsync("cPanel Banner");
            mockNet.Setup(n => n.ExtractSslCertificateAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
                   .Callback<string, int, string>((target, _, sni) =>
                   {
                       capturedSslTarget = target;
                       capturedSslSni = sni;
                   })
                   .ReturnsAsync(SslCertificateInfo.NotAvailable());

            var mockAuth = new Mock<IAuthenticationVerifier>();
            mockAuth.Setup(a => a.VerifyCPanelAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                    .ReturnsAsync(AuthVerificationResult.Valid("cPanel ok"));

            var mockContext = new Mock<IContextExtractor>();
            var mockEntropy = new Mock<IEntropyAnalyzer>();
            var mockOsint = new Mock<IOSINTService>();
            mockOsint.Setup(o => o.IsHoneypotAsync(It.IsAny<string>())).ReturnsAsync(false);
            mockOsint.Setup(o => o.QueryGreyNoiseAsync(It.IsAny<string>()))
                     .ReturnsAsync(new GreyNoiseResult { Classification = "benign", IsBot = false });
            var mockGeo = new Mock<IGeolocationService>();
            mockGeo.Setup(g => g.GeolocateAsync(It.IsAny<string>()))
                   .ReturnsAsync(new GeolocationResult { Country = "US", ISP = "TestISP" });

            var provider = new ServerCredentialProvider(
                mockNet.Object, mockAuth.Object, mockContext.Object,
                mockEntropy.Object, mockOsint.Object, mockGeo.Object,
                mockDns.Object);

            // cPanel on port 2083 triggers SSL extraction
            var input = "cpanel://admin:pass123@cp.example.com:2083";

            var validateMethod = typeof(ServerCredentialProvider)
                .GetMethod("ValidateKeyWithHttpClientAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            using var httpClient = new System.Net.Http.HttpClient();
            var task = (Task)validateMethod!.Invoke(provider, new object[] { input, httpClient })!;
            await task;

            // Assert: SSL extraction received IP for TCP, hostname for SNI
            Assert.Equal("203.0.113.50", capturedSslTarget);
            Assert.Equal("cp.example.com", capturedSslSni);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  D. HTTP verifier pinning (cPanel/WHM/Plesk):
        //     TCP → validated IP, Host/SNI → original hostname
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public async Task FullFlow_CPanel_ReceivesIpAndHostnameSeparately()
        {
            var mockDns = new Mock<IDnsResolver>();
            mockDns.Setup(d => d.ResolveAsync("cp.example.com"))
                   .ReturnsAsync(new[] { IPAddress.Parse("203.0.113.50") });

            string? capturedTargetAddress = null;
            string? capturedOriginalHostname = null;

            var mockNet = new Mock<INetworkVerifier>();
            mockNet.Setup(n => n.VerifyConnectivityAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                   .ReturnsAsync(NetworkVerificationResult.Success("203.0.113.50", 2083));
            mockNet.Setup(n => n.ExtractBannerAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
                   .ReturnsAsync("cPanel");
            mockNet.Setup(n => n.ExtractSslCertificateAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
                   .ReturnsAsync(SslCertificateInfo.NotAvailable());

            var mockAuth = new Mock<IAuthenticationVerifier>();
            mockAuth.Setup(a => a.VerifyCPanelAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                    .Callback<string, int, string, string, string>((target, _, _, _, hostname) =>
                    {
                        capturedTargetAddress = target;
                        capturedOriginalHostname = hostname;
                    })
                    .ReturnsAsync(AuthVerificationResult.Valid("cPanel ok"));

            var mockContext = new Mock<IContextExtractor>();
            var mockEntropy = new Mock<IEntropyAnalyzer>();
            var mockOsint = new Mock<IOSINTService>();
            mockOsint.Setup(o => o.IsHoneypotAsync(It.IsAny<string>())).ReturnsAsync(false);
            mockOsint.Setup(o => o.QueryGreyNoiseAsync(It.IsAny<string>()))
                     .ReturnsAsync(new GreyNoiseResult { Classification = "benign", IsBot = false });
            var mockGeo = new Mock<IGeolocationService>();
            mockGeo.Setup(g => g.GeolocateAsync(It.IsAny<string>()))
                   .ReturnsAsync(new GeolocationResult { Country = "US", ISP = "TestISP" });

            var provider = new ServerCredentialProvider(
                mockNet.Object, mockAuth.Object, mockContext.Object,
                mockEntropy.Object, mockOsint.Object, mockGeo.Object,
                mockDns.Object);

            var input = "cpanel://admin:pass123@cp.example.com:2083";

            var validateMethod = typeof(ServerCredentialProvider)
                .GetMethod("ValidateKeyWithHttpClientAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            using var httpClient = new System.Net.Http.HttpClient();
            var task = (Task)validateMethod!.Invoke(provider, new object[] { input, httpClient })!;
            await task;

            // Assert: auth verifier received IP for TCP connection
            Assert.Equal("203.0.113.50", capturedTargetAddress);
            // Assert: auth verifier received original hostname for Host/SNI
            Assert.Equal("cp.example.com", capturedOriginalHostname);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Original hostname preserved in credential metadata
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void ParseCredential_PreservesOriginalHostname()
        {
            var provider = new ServerCredentialProvider();
            var method = typeof(ServerCredentialProvider)
                .GetMethod("ParseCredentialAndGetRawPassword",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            Assert.NotNull(method);

            var result = method!.Invoke(provider, new object[] { "ftp://user1:pass123@ftp.example.com:21" });
            Assert.NotNull(result);

            var cred = result!.GetType().GetField("Item1")?.GetValue(result) as ServerCredential;
            Assert.NotNull(cred);

            // Host must be the original hostname, not an IP
            Assert.Equal("ftp.example.com", cred!.Host);
        }
    }
}
