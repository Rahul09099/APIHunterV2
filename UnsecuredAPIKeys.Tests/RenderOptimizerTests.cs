using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using FsCheck;
using FsCheck.Xunit;
using UnsecuredAPIKeys.Providers.ServerProviders.Services;

namespace UnsecuredAPIKeys.Tests
{
    public class RenderOptimizerTests
    {
        [Fact]
        public void RenderOptimizer_DetectsRenderFreeTier_FromEnvironment()
        {
            // Arrange
            Environment.SetEnvironmentVariable("RENDER_FREE_TIER", "true");
            var optimizer = new RenderOptimizer();

            // Assert
            Assert.True(optimizer.IsRenderFreeTier);
            Assert.Equal(2, optimizer.MaxConcurrentScans);
            Assert.Equal(1, optimizer.MaxConcurrentVerify);
            Assert.Equal(10, optimizer.VerificationBatchSize);
            Assert.Equal(32768, optimizer.BufferSizeBytes);
        }

        [Fact]
        public void RenderOptimizer_StandardTier_WhenEnvNotSet()
        {
            // Arrange
            Environment.SetEnvironmentVariable("RENDER_FREE_TIER", null);
            Environment.SetEnvironmentVariable("RENDER_INSTANCE_TYPE", null);
            var optimizer = new RenderOptimizer();

            // Assert
            Assert.False(optimizer.IsRenderFreeTier);
            Assert.Equal(10, optimizer.MaxConcurrentScans);
            Assert.Equal(5, optimizer.MaxConcurrentVerify);
            Assert.Equal(50, optimizer.VerificationBatchSize);
            Assert.Equal(65536, optimizer.BufferSizeBytes);
        }

        [Fact]
        public async Task CheckMemoryPressureAsync_RunsWithoutError()
        {
            var optimizer = new RenderOptimizer();
            await optimizer.CheckMemoryPressureAsync();
            // Should successfully check memory pressure without exception
            Assert.NotNull(optimizer);
        }

        // Op enum for property-based test
        public enum ConcurrencyOp { StartScan, EndScan, StartVerify, EndVerify }

        [Property(MaxTest = 100)]
        public bool Property_P6_RenderFreeTierConcurrencyInvariant(List<ConcurrencyOp> ops)
        {
            Environment.SetEnvironmentVariable("RENDER_FREE_TIER", "true");
            var optimizer = new RenderOptimizer();

            int activeScans = 0;
            int activeVerifications = 0;

            foreach (var op in ops)
            {
                switch (op)
                {
                    case ConcurrencyOp.StartScan:
                        if (activeScans < optimizer.MaxConcurrentScans)
                        {
                            activeScans++;
                        }
                        break;
                    case ConcurrencyOp.EndScan:
                        if (activeScans > 0)
                        {
                            activeScans--;
                        }
                        break;
                    case ConcurrencyOp.StartVerify:
                        if (activeVerifications < optimizer.MaxConcurrentVerify)
                        {
                            activeVerifications++;
                        }
                        break;
                    case ConcurrencyOp.EndVerify:
                        if (activeVerifications > 0)
                        {
                            activeVerifications--;
                        }
                        break;
                }

                // Invariant Check
                if (activeScans > 2 || activeVerifications > 1)
                {
                    return false; // Invariant violated
                }
            }

            return true;
        }
    }
}
