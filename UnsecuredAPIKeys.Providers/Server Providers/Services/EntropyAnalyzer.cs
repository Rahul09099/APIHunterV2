using System;
using System.Collections.Generic;

namespace UnsecuredAPIKeys.Providers.ServerProviders.Services
{
    public interface IEntropyAnalyzer
    {
        double CalculateEntropy(string input);
        bool IsHighEntropyPassword(string input, double threshold = 4.0);
    }

    public class EntropyAnalyzer : IEntropyAnalyzer
    {
        public double CalculateEntropy(string input)
        {
            if (string.IsNullOrEmpty(input))
                return 0.0;
            
            var frequency = new Dictionary<char, int>();
            foreach (var c in input)
            {
                if (frequency.ContainsKey(c))
                    frequency[c]++;
                else
                    frequency[c] = 1;
            }
            
            double entropy = 0.0;
            var length = input.Length;
            
            foreach (var count in frequency.Values)
            {
                var probability = (double)count / length;
                entropy -= probability * Math.Log2(probability);
            }
            
            return entropy;
        }
        
        public bool IsHighEntropyPassword(string input, double threshold = 4.0)
        {
            return CalculateEntropy(input) >= threshold;
        }
    }
}
