using System;
using System.IO;
using System.Threading.Tasks;

namespace UnsecuredAPIKeys.Providers.ServerProviders.Services
{
    public class AdaptiveIOManager
    {
        private IOStrategy _selectedStrategy = IOStrategy.Streaming;

        public enum IOStrategy { DirectIO, Streaming }

        public IOStrategy SelectedStrategy => _selectedStrategy;

        public async Task InitializeAsync()
        {
            try
            {
                _selectedStrategy = await BenchmarkAndSelectAsync();
            }
            catch
            {
                _selectedStrategy = IOStrategy.Streaming;
            }
        }

        private async Task<IOStrategy> BenchmarkAndSelectAsync()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(tempFile, new byte[1_048_576]); // 1MB temp file

                var directMs = await MeasureDirectIOAsync(tempFile);
                var streamMs = await MeasureStreamingAsync(tempFile);

                // Use Direct I/O only if it is at least 20% faster
                return directMs * 1.2 < streamMs ? IOStrategy.DirectIO : IOStrategy.Streaming;
            }
            catch
            {
                return IOStrategy.Streaming;
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        private async Task<long> MeasureDirectIOAsync(string path)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 3; i++)
            {
                await ReadDirectIOAsync(path);
            }
            return sw.ElapsedMilliseconds;
        }

        private async Task<long> MeasureStreamingAsync(string path)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 3; i++)
            {
                await ReadStreamingAsync(path);
            }
            return sw.ElapsedMilliseconds;
        }

        public async Task<string> ReadFileAsync(string path, int maxSizeBytes = 10_485_760)
        {
            var info = new FileInfo(path);
            if (info.Length > maxSizeBytes)
                return await ReadStreamingAsync(path);   // always stream oversized files

            return _selectedStrategy == IOStrategy.DirectIO
                ? await ReadDirectIOAsync(path)
                : await ReadStreamingAsync(path);
        }

        private async Task<string> ReadStreamingAsync(string path)
        {
            using var reader = new StreamReader(path, System.Text.Encoding.UTF8, true, 32_768);
            return await reader.ReadToEndAsync();
        }

        private async Task<string> ReadDirectIOAsync(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 32_768, FileOptions.SequentialScan);
            using var reader = new StreamReader(fs);
            return await reader.ReadToEndAsync();
        }
    }
}
