using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using FsCheck;
using FsCheck.Xunit;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers.ServerProviders.Services;

namespace UnsecuredAPIKeys.Tests
{
    public class NetworkVerifierTests
    {
        private readonly NetworkVerifier _verifier = new();

        [Fact]
        public async Task VerifyConnectivityAsync_UnreachableHost_ReturnsUnreachable()
        {
            // Use an address from the documentation/TEST-NET-1 (192.0.2.0/24) range
            // which is guaranteed to be unreachable.
            var result = await _verifier.VerifyConnectivityAsync("192.0.2.1", 12345, timeoutSeconds: 1);

            Assert.False(result.IsAccessible);
            Assert.Contains(result.Status, new[] { "Unreachable", "Timeout" });
        }

        [Fact]
        public async Task ExtractBannerAsync_UnreachableHost_ReturnsUnreachableOrTimeout()
        {
            var banner = await _verifier.ExtractBannerAsync("192.0.2.1", 12345, timeoutSeconds: 1);
            Assert.Contains(banner, new[] { "Unreachable", "Timeout", "Banner extraction failed" });
        }

        [Fact]
        public async Task ExtractSslCertificateAsync_NonSslPort_ReturnsError()
        {
            // Connect to a known non-SSL service or unreachable host
            var certInfo = await _verifier.ExtractSslCertificateAsync("192.0.2.1", 12345);
            Assert.NotNull(certInfo);
            Assert.True(certInfo.Subject == "Timeout connecting" || certInfo.Subject == "Unreachable" || certInfo.Subject.Contains("unreachable") || certInfo.Subject.Contains("failed") || certInfo.Subject.Contains("Connection") || certInfo.Subject.Contains("Error"));
        }

        [Fact]
        public async Task NetworkVerificationResult_FactoryMethods_ProduceCorrectStatus()
        {
            var success = NetworkVerificationResult.Success("127.0.0.1", 80);
            var unreachable = NetworkVerificationResult.Unreachable();
            var timeout = NetworkVerificationResult.Timeout();
            var error = NetworkVerificationResult.Error("Some error");

            Assert.True(success.IsAccessible);
            Assert.Equal("Accessible", success.Status);

            Assert.False(unreachable.IsAccessible);
            Assert.Equal("Unreachable", unreachable.Status);

            Assert.False(timeout.IsAccessible);
            Assert.Equal("Timeout", timeout.Status);

            Assert.False(error.IsAccessible);
            Assert.Equal("Error", error.Status);
            Assert.Equal("Some error", error.ErrorMessage);
        }

        [Property(MaxTest = 20)]
        public bool Property_P4_NetworkTimeoutEnforcement(PositiveInt timeoutSecs)
        {
            // Ensure timeout is small for testing to avoid slowing down test suite
            int timeout = (timeoutSecs.Get % 2) + 1; // 1 or 2 seconds
            
            var stopwatch = Stopwatch.StartNew();
            var task = _verifier.VerifyConnectivityAsync("192.0.2.1", 12345, timeoutSeconds: timeout);
            task.Wait();
            stopwatch.Stop();

            // The call must complete within timeoutSeconds + 1.5 seconds (allowing for small OS overhead)
            return stopwatch.Elapsed.TotalSeconds <= (timeout + 1.5);
        }
    }
}
