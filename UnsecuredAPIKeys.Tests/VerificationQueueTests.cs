using System;
using System.Threading.Tasks;
using Xunit;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers.ServerProviders.Services;

namespace UnsecuredAPIKeys.Tests
{
    public class VerificationQueueTests
    {
        [Fact]
        public async Task Queue_OrdersByPriorityCorrectly()
        {
            // Arrange
            var queue = new VerificationQueue();
            
            var c1 = new ServerCredential { Host = "host1", RiskLevel = "Low" };
            var c2 = new ServerCredential { Host = "host2", RiskLevel = "Critical" };
            var c3 = new ServerCredential { Host = "host3", RiskLevel = "High" };
            var c4 = new ServerCredential { Host = "host4", RiskLevel = "Medium" };

            // Act
            await queue.EnqueueAsync(c1);
            await queue.EnqueueAsync(c2);
            await queue.EnqueueAsync(c3);
            await queue.EnqueueAsync(c4);

            // Assert: Critical(0) -> High(1) -> Medium(2) -> Low(3)
            var first = await queue.DequeueAsync();
            var second = await queue.DequeueAsync();
            var third = await queue.DequeueAsync();
            var fourth = await queue.DequeueAsync();

            Assert.Equal("Critical", first?.RiskLevel);
            Assert.Equal("High", second?.RiskLevel);
            Assert.Equal("Medium", third?.RiskLevel);
            Assert.Equal("Low", fourth?.RiskLevel);
        }

        [Fact]
        public async Task Queue_EnforcesMaxLimit()
        {
            var queue = new VerificationQueue();
            
            // Limit is 1000
            for (int i = 0; i < 1100; i++)
            {
                await queue.EnqueueAsync(new ServerCredential { Host = $"host{i}", RiskLevel = "Low" });
            }

            var count = await queue.GetCountAsync();
            Assert.Equal(1000, count);
        }

        [Fact]
        public async Task Queue_AppliesExponentialBackoffDelays()
        {
            var queue = new VerificationQueue();
            var host = "backoff-host";

            // Initial state: can process immediately
            Assert.True(await queue.CanProcessHostAsync(host));

            // Record 1st failure (backoff: 10s)
            await queue.RecordAttemptAsync(host, success: false);
            Assert.False(await queue.CanProcessHostAsync(host));

            // Record 2nd failure (backoff: 30s)
            await queue.RecordAttemptAsync(host, success: false);
            Assert.False(await queue.CanProcessHostAsync(host));

            // Record success (resets failures)
            await queue.RecordAttemptAsync(host, success: true);
            
            // Success resets failure count, but there's a 10s minimum delay.
            // Let's use reflection to bypass the minimum delay by shifting the last attempt time back 11 seconds.
            var field = typeof(VerificationQueue).GetField("_lastAttemptTimes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var dict = field?.GetValue(queue) as System.Collections.Generic.Dictionary<string, DateTime>;
            if (dict != null)
            {
                dict[host] = DateTime.UtcNow.AddSeconds(-11);
            }

            Assert.True(await queue.CanProcessHostAsync(host));
        }

        [Fact]
        public void CircuitBreaker_OpensAfterThresholdFailures()
        {
            var cb = new HostCircuitBreaker();
            var host = "circuit-host";

            Assert.False(cb.IsOpen(host));

            // Record 1st and 2nd failures
            cb.RecordFailure(host);
            cb.RecordFailure(host);
            Assert.False(cb.IsOpen(host));

            // Record 3rd failure -> Open
            cb.RecordFailure(host);
            Assert.True(cb.IsOpen(host));

            // Record success -> Close
            cb.RecordSuccess(host);
            Assert.False(cb.IsOpen(host));
        }
    }
}
