using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace UnsecuredAPIKeys.Providers.ServerProviders.Services
{
    public class OSINTResult
    {
        public string Source { get; set; } = string.Empty;
        public string Data { get; set; } = "{}";
    }

    public class GreyNoiseResult
    {
        public string Classification { get; set; } = "unknown"; // benign | malicious | unknown
        public bool IsBot { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public interface IOSINTService
    {
        Task<OSINTResult> QueryShodanAsync(string ipAddress);
        Task<OSINTResult> QueryCensysAsync(string ipAddress);
        Task<GreyNoiseResult> QueryGreyNoiseAsync(string ipAddress);
        Task<bool> IsHoneypotAsync(string ipAddress);
    }

    public class OSINTService : IOSINTService
    {
        private readonly SemaphoreSlim _rateLimiter = new(1, 1);
        private readonly TimeSpan _requestDelay = TimeSpan.FromSeconds(5);
        private readonly IMemoryCache _cache;
        private readonly ILogger<OSINTService>? _logger;

        public OSINTService(IMemoryCache cache, ILogger<OSINTService>? logger = null)
        {
            _cache = cache;
            _logger = logger;
        }

        public async Task<OSINTResult> QueryShodanAsync(string ipAddress)
        {
            var cacheKey = $"shodan_{ipAddress}";
            if (_cache.TryGetValue(cacheKey, out OSINTResult? cached) && cached != null)
                return cached;

            await _rateLimiter.WaitAsync();
            try
            {
                await Task.Delay(_requestDelay);
                var result = new OSINTResult { Source = "Shodan", Data = "{}" };
                _cache.Set(cacheKey, result, TimeSpan.FromHours(24));
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Shodan query failed for {IP}", ipAddress);
                return new OSINTResult { Source = "Shodan", Data = "{}" };
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        public async Task<OSINTResult> QueryCensysAsync(string ipAddress)
        {
            var cacheKey = $"censys_{ipAddress}";
            if (_cache.TryGetValue(cacheKey, out OSINTResult? cached) && cached != null)
                return cached;

            await _rateLimiter.WaitAsync();
            try
            {
                await Task.Delay(_requestDelay);
                var result = new OSINTResult { Source = "Censys", Data = "{}" };
                _cache.Set(cacheKey, result, TimeSpan.FromHours(24));
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Censys query failed for {IP}", ipAddress);
                return new OSINTResult { Source = "Censys", Data = "{}" };
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        public async Task<GreyNoiseResult> QueryGreyNoiseAsync(string ipAddress)
        {
            var cacheKey = $"greynoise_{ipAddress}";
            if (_cache.TryGetValue(cacheKey, out GreyNoiseResult? cached) && cached != null)
                return cached;

            await _rateLimiter.WaitAsync();
            try
            {
                await Task.Delay(_requestDelay);
                
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                
                var url = $"https://api.greynoise.io/v3/community/{ipAddress}";
                var response = await client.GetAsync(url);
                
                var result = new GreyNoiseResult();
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    if (content.Contains("\"malicious\""))
                    {
                        result.Classification = "malicious";
                    }
                    else if (content.Contains("\"benign\""))
                    {
                        result.Classification = "benign";
                    }
                    
                    if (content.Contains("\"bot\":true"))
                    {
                        result.IsBot = true;
                    }
                }
                
                _cache.Set(cacheKey, result, TimeSpan.FromHours(24));
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "GreyNoise community API query failed for {IP}", ipAddress);
                return new GreyNoiseResult();
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        public async Task<bool> IsHoneypotAsync(string ipAddress)
        {
            try
            {
                var result = await QueryGreyNoiseAsync(ipAddress);
                return result != null && (result.Classification == "malicious" || result.IsBot);
            }
            catch
            {
                return false;
            }
        }
    }
}
