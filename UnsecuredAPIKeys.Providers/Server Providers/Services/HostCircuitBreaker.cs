using System;
using System.Collections.Concurrent;

namespace UnsecuredAPIKeys.Providers.ServerProviders.Services
{
    public class CircuitState
    {
        public int Failures { get; set; }
        public DateTime? OpenUntil { get; set; }
        public bool IsOpen => OpenUntil.HasValue && OpenUntil.Value > DateTime.UtcNow;
    }

    public class HostCircuitBreaker
    {
        private readonly ConcurrentDictionary<string, CircuitState> _states = new();
        private const int FailureThreshold = 3;

        public bool IsOpen(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            return _states.TryGetValue(host, out var state) && state.IsOpen;
        }

        public void RecordFailure(string host)
        {
            if (string.IsNullOrEmpty(host)) return;

            var state = _states.GetOrAdd(host, _ => new CircuitState());
            lock (state)
            {
                state.Failures++;
                if (state.Failures >= FailureThreshold)
                {
                    state.OpenUntil = DateTime.UtcNow.AddMinutes(30);
                }
            }
        }

        public void RecordSuccess(string host)
        {
            if (string.IsNullOrEmpty(host)) return;

            var state = _states.GetOrAdd(host, _ => new CircuitState());
            lock (state)
            {
                state.Failures = 0;
                state.OpenUntil = null;
            }
        }
    }
}
