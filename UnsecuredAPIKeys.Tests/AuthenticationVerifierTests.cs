using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Xunit;
using FsCheck;
using FsCheck.Xunit;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers.ServerProviders.Services;

namespace UnsecuredAPIKeys.Tests
{
    public class AuthenticationVerifierTests
    {
        private static IMemoryCache CreateCache() =>
            new MemoryCache(new MemoryCacheOptions());

        [Fact]
        public async Task Cooldown_EnforcesSingleAttempt()
        {
            // Arrange
            var cache = CreateCache();
            var verifier = new AuthenticationVerifier(cache);
            var host = "192.0.2.1";
            var port = 22;
            var username = "admin";
            var password = "password";

            // Act: First attempt (should trigger SSH call and set cooldown)
            var hash = verifier.ComputeHash(host, port, username);
            Assert.False(verifier.IsOnCooldown(hash));
            
            var result1 = await verifier.VerifySSHAsync(host, port, username, password);
            
            // Assert: Cooldown is active
            Assert.True(verifier.IsOnCooldown(hash));

            // Act: Second attempt immediately after
            var result2 = await verifier.VerifySSHAsync(host, port, username, password);

            // Assert: Second attempt is rate-limited (returned RateLimited status)
            Assert.Equal("RateLimited", result2.Status);
            Assert.False(result2.IsValid);
        }

        [Fact]
        public void ComputeHash_ProducesConsistentOutput()
        {
            var cache = CreateCache();
            var verifier = new AuthenticationVerifier(cache);
            
            var hash1 = verifier.ComputeHash("example.com", 80, "user");
            var hash2 = verifier.ComputeHash("example.com", 80, "user");
            var hash3 = verifier.ComputeHash("example.com", 80, "other");

            Assert.Equal(hash1, hash2);
            Assert.NotEqual(hash1, hash3);
        }

        [Property(MaxTest = 100)]
        public bool Property_P3_SingleAuthenticationAttemptPerDay(NonNull<string> hostGen, PositiveInt portGen, NonNull<string> usernameGen)
        {
            var host = hostGen.Get;
            var username = usernameGen.Get;
            var port = portGen.Get;

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(username))
                return true;

            var cache = CreateCache();
            var verifier = new AuthenticationVerifier(cache);

            // Compute hash for credential
            var hash = verifier.ComputeHash(host, port, username);

            // 1. Initially should not be on cooldown
            if (verifier.IsOnCooldown(hash))
                return false;

            // 2. Perform a verify attempt (which internally checks and sets cooldown)
            // We use RDP verifier as a safe non-blocking option that we mock with a test-net IP
            var result1 = verifier.VerifyRDPAsync(host, port, username, "somepass").Result;

            // 3. Immediately after, it MUST be on cooldown
            if (!verifier.IsOnCooldown(hash))
                return false;

            // 4. A subsequent attempt must return RateLimited
            var result2 = verifier.VerifyRDPAsync(host, port, username, "somepass").Result;
            
            return result2.Status == "RateLimited" && !result2.IsValid;
        }
    }
}
