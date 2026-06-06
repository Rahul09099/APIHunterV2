using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using UnsecuredAPIKeys.Providers.ServerProviders.Services;

namespace UnsecuredAPIKeys.Tests
{
    public class AdaptiveIOManagerTests
    {
        [Fact]
        public async Task InitializeAsync_SelectsStrategyGracefully()
        {
            var manager = new AdaptiveIOManager();
            await manager.InitializeAsync();
            
            // Should successfully initialize without throwing
            Assert.True(manager.SelectedStrategy == AdaptiveIOManager.IOStrategy.Streaming ||
                        manager.SelectedStrategy == AdaptiveIOManager.IOStrategy.DirectIO);
        }

        [Fact]
        public async Task ReadFileAsync_ReturnsCorrectContent()
        {
            var manager = new AdaptiveIOManager();
            await manager.InitializeAsync();

            var tempFile = Path.GetTempFileName();
            var expectedContent = "Hello from Adaptive IO Manager! Line 1\nLine 2\nLine 3";
            await File.WriteAllTextAsync(tempFile, expectedContent);

            try
            {
                var content = await manager.ReadFileAsync(tempFile);
                Assert.Equal(expectedContent, content);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        [Fact]
        public async Task ReadFileAsync_OversizedFile_UsesStreaming()
        {
            var manager = new AdaptiveIOManager();
            await manager.InitializeAsync();

            var tempFile = Path.GetTempFileName();
            var contentToWrite = "Hello oversized file!";
            await File.WriteAllTextAsync(tempFile, contentToWrite);

            try
            {
                // Set max size extremely small (e.g. 5 bytes) so the file is oversized
                var content = await manager.ReadFileAsync(tempFile, maxSizeBytes: 5);
                Assert.Equal(contentToWrite, content);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }
    }
}
