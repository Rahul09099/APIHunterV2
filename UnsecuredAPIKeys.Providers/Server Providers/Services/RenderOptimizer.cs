using System;
using System.Threading.Tasks;

namespace UnsecuredAPIKeys.Providers.ServerProviders.Services
{
    public class RenderOptimizer
    {
        private readonly bool _isRenderFreeTier;

        public RenderOptimizer()
        {
            _isRenderFreeTier = Environment.GetEnvironmentVariable("RENDER_FREE_TIER") == "true"
                                || Environment.GetEnvironmentVariable("RENDER_INSTANCE_TYPE") == "free";
        }

        public bool IsRenderFreeTier => _isRenderFreeTier;
        public int MaxConcurrentScans => _isRenderFreeTier ? 2 : 10;
        public int MaxConcurrentVerify => _isRenderFreeTier ? 1 : 5;
        public int VerificationBatchSize => _isRenderFreeTier ? 10 : 50;
        public int MaxFileSizeBytes => _isRenderFreeTier ? 10_485_760 : 104_857_600; // 10MB vs 100MB
        public int MaxFilesPerScan => _isRenderFreeTier ? 100 : 1000;
        public int BufferSizeBytes => _isRenderFreeTier ? 32_768 : 65_536; // 32KB vs 64KB
        public long MemoryThresholdBytes => 400L * 1024 * 1024; // 400 MB

        public async Task CheckMemoryPressureAsync()
        {
            var used = GC.GetTotalMemory(false);
            if (used > MemoryThresholdBytes)
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                await Task.Delay(500);
            }
        }
    }
}
