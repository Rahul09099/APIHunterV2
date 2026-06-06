using System;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using UnsecuredAPIKeys.Providers.ServerProviders.Services;

namespace UnsecuredAPIKeys.Tests
{
    public class EntropyAnalyzerTests
    {
        private readonly EntropyAnalyzer _analyzer = new();

        [Fact]
        public void CalculateEntropy_EmptyOrNull_ReturnsZero()
        {
            Assert.Equal(0.0, _analyzer.CalculateEntropy(string.Empty));
            Assert.Equal(0.0, _analyzer.CalculateEntropy(null!));
        }

        [Fact]
        public void CalculateEntropy_SingleCharacter_ReturnsZero()
        {
            Assert.Equal(0.0, _analyzer.CalculateEntropy("a"));
            Assert.Equal(0.0, _analyzer.CalculateEntropy("aaaaa"));
        }

        [Fact]
        public void IsHighEntropyPassword_RespectsThreshold()
        {
            Assert.False(_analyzer.IsHighEntropyPassword("password", 3.5));
            Assert.True(_analyzer.IsHighEntropyPassword("p@ssw0rd!123", 3.0));
        }

        // Reference implementation of Shannon Entropy in test
        private static double ReferenceShannonEntropy(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0.0;
            var freqs = s.GroupBy(c => c).Select(g => g.Count());
            double len = s.Length;
            return freqs.Select(count => count / len)
                        .Sum(p => -p * Math.Log2(p));
        }

        [Property(MaxTest = 100)]
        public bool Property_P7_EntropyScoreAccuracy(NonNull<string> input)
        {
            var str = input.Get;
            if (string.IsNullOrEmpty(str)) return true;

            var actual = _analyzer.CalculateEntropy(str);
            var expected = ReferenceShannonEntropy(str);

            return Math.Abs(actual - expected) < 1e-9;
        }
    }
}
