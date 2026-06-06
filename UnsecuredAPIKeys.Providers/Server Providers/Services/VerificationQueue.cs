using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Providers.ServerProviders.Services
{
    public class VerificationQueue
    {
        private readonly PriorityQueue<ServerCredential, int> _queue = new();
        private readonly SemaphoreSlim _lock = new(1, 1);
        private const int MaxQueueSize = 1000;

        private readonly Dictionary<string, DateTime> _lastAttemptTimes = new();
        private readonly Dictionary<string, int> _failureCounts = new();

        public async Task<int> GetCountAsync()
        {
            await _lock.WaitAsync();
            try
            {
                return _queue.Count;
            }
            finally { _lock.Release(); }
        }

        public async Task EnqueueAsync(ServerCredential credential)
        {
            await _lock.WaitAsync();
            try
            {
                if (_queue.Count >= MaxQueueSize) return;
                var priority = GetPriority(credential.RiskLevel);
                _queue.Enqueue(credential, priority);
            }
            finally { _lock.Release(); }
        }

        public async Task<ServerCredential?> DequeueAsync()
        {
            await _lock.WaitAsync();
            try
            {
                return _queue.TryDequeue(out var item, out _) ? item : null;
            }
            finally { _lock.Release(); }
        }

        public async Task<bool> CanProcessHostAsync(string host)
        {
            await _lock.WaitAsync();
            try
            {
                if (_lastAttemptTimes.TryGetValue(host, out var lastTime))
                {
                    var delay = TimeSpan.FromSeconds(10);
                    if (_failureCounts.TryGetValue(host, out var fails) && fails > 0)
                    {
                        delay = fails switch
                        {
                            1 => TimeSpan.FromSeconds(10),
                            2 => TimeSpan.FromSeconds(30),
                            3 => TimeSpan.FromSeconds(60),
                            _ => TimeSpan.FromSeconds(300)
                        };
                    }

                    if (DateTime.UtcNow - lastTime < delay)
                    {
                        return false;
                    }
                }
                return true;
            }
            finally { _lock.Release(); }
        }

        public async Task RecordAttemptAsync(string host, bool success)
        {
            await _lock.WaitAsync();
            try
            {
                _lastAttemptTimes[host] = DateTime.UtcNow;
                if (success)
                {
                    _failureCounts[host] = 0;
                }
                else
                {
                    if (_failureCounts.TryGetValue(host, out var fails))
                        _failureCounts[host] = fails + 1;
                    else
                        _failureCounts[host] = 1;
                }
            }
            finally { _lock.Release(); }
        }

        private int GetPriority(string riskLevel)
        {
            return riskLevel switch
            {
                "Critical" => 0,
                "High" => 1,
                "Medium" => 2,
                "Low" => 3,
                _ => 3
            };
        }
    }
}
