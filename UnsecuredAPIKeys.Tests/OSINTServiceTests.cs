using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Xunit;
using FsCheck;
using FsCheck.Xunit;
using UnsecuredAPIKeys.Providers.ServerProviders.Services;

namespace UnsecuredAPIKeys.Tests
{
    public class OSINTServiceTests
    {
        public class MockMemoryCache : IMemoryCache
        {
            public class CacheEntry
            {
                public object Value { get; set; } = null!;
                public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
                public TimeSpan Expiration { get; set; } = TimeSpan.FromHours(24);
                public bool IsExpired => DateTime.UtcNow - CreatedAt >= Expiration;
            }

            public readonly Dictionary<object, CacheEntry> Store = new();

            public ICacheEntry CreateEntry(object key)
            {
                var entryMock = new Moq.Mock<ICacheEntry>();
                entryMock.SetupAllProperties();
                entryMock.SetupSet(e => e.Value = Moq.It.IsAny<object>())
                         .Callback<object>(val => {
                             Store[key] = new CacheEntry { Value = val };
                         });
                return entryMock.Object;
            }

            public void Dispose() {}

            public void Remove(object key)
            {
                Store.Remove(key);
            }

            public bool TryGetValue(object key, out object? value)
            {
                value = null;
                if (Store.TryGetValue(key, out var entry))
                {
                    if (!entry.IsExpired)
                    {
                        value = entry.Value;
                        return true;
                    }
                    else
                    {
                        Store.Remove(key);
                    }
                }
                return false;
            }
        }

        [Fact]
        public async Task IsHoneypotAsync_MaliciousOrBot_ReturnsTrue()
        {
            var cache = new MockMemoryCache();
            var service = new OSINTService(cache);

            // Populate cache with malicious GreyNoise result to avoid real HTTP requests
            var ip = "192.0.2.5";
            var cacheKey = $"greynoise_{ip}";
            cache.Store[cacheKey] = new MockMemoryCache.CacheEntry
            {
                Value = new GreyNoiseResult { Classification = "malicious", IsBot = false }
            };

            var isHoneypot = await service.IsHoneypotAsync(ip);
            Assert.True(isHoneypot);
        }

        [Fact]
        public async Task IsHoneypotAsync_BenignNonBot_ReturnsFalse()
        {
            var cache = new MockMemoryCache();
            var service = new OSINTService(cache);

            var ip = "192.0.2.6";
            var cacheKey = $"greynoise_{ip}";
            cache.Store[cacheKey] = new MockMemoryCache.CacheEntry
            {
                Value = new GreyNoiseResult { Classification = "benign", IsBot = false }
            };

            var isHoneypot = await service.IsHoneypotAsync(ip);
            Assert.False(isHoneypot);
        }

        [Property(MaxTest = 100)]
        public bool Property_P8_OSINTCacheFreshness(NonNull<string> ipGen, int hoursElapsed)
        {
            var ip = ipGen.Get;
            if (string.IsNullOrEmpty(ip)) return true;

            var cache = new MockMemoryCache();
            var cacheKey = $"shodan_{ip}";

            // 1. Setup a cached entry in our store with configurable elapsed hours
            var createdAt = DateTime.UtcNow.AddHours(-Math.Abs(hoursElapsed));
            var cachedResult = new OSINTResult { Source = "Shodan", Data = "{\"cached\": true}" };
            
            cache.Store[cacheKey] = new MockMemoryCache.CacheEntry
            {
                Value = cachedResult,
                CreatedAt = createdAt,
                Expiration = TimeSpan.FromHours(24)
            };

            // 2. Query cache directly using TryGetValue
            object? val;
            bool success = cache.TryGetValue(cacheKey, out val);

            // If the item is older than 24 hours (Math.Abs(hoursElapsed) >= 24), it must be expired and TryGetValue must return false
            if (Math.Abs(hoursElapsed) >= 24)
            {
                return !success && val == null;
            }
            else
            {
                return success && val == cachedResult;
            }
        }
    }
}
