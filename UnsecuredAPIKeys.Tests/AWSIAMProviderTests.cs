using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers.Cloud_Providers;
using UnsecuredAPIKeys.Providers.Common;
using Xunit;

namespace UnsecuredAPIKeys.Tests
{
    public class AWSIAMProviderTests
    {
        [Theory]
        [InlineData("AKIA1234567890ABCDEF")]
        [InlineData("ASIA1234567890ABCDEF")]
        [InlineData("AKIA1234567890ABCDEF:::TEST_SECRET_KEY_FOR_UNIT_TEST_ONLY_1234")]
        [InlineData("ASIA1234567890ABCDEF|TEST_SECRET_KEY_FOR_UNIT_TEST_ONLY_1234")]
        public void RegexPatterns_MatchValidAWSFormatCandidates(string credentialCandidate)
        {
            var provider = new AWSIAMProvider();
            var matches = false;
            foreach (var pattern in provider.RegexPatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(credentialCandidate, pattern))
                {
                    matches = true;
                    break;
                }
            }

            Assert.True(matches, $"Candidate '{credentialCandidate}' should match AWS IAM provider regex patterns.");
        }

        [Theory]
        [InlineData("INVALID_KEY_NAME")]
        [InlineData("AKIA_TOO_SHORT")]
        [InlineData("AKIA1234567890ABCDEFGH_TOO_LONG")]
        public void RegexPatterns_RejectsMalformedAWSKeys(string invalidKey)
        {
            var provider = new AWSIAMProvider();
            var matches = false;
            foreach (var pattern in provider.RegexPatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(invalidKey, pattern))
                {
                    matches = true;
                    break;
                }
            }

            Assert.False(matches, $"Invalid key '{invalidKey}' should NOT match AWS IAM provider regex patterns.");
        }

        [Fact]
        public async Task ValidateKeyAsync_AccessKeyWithoutSecret_ReturnsProviderErrorForMissingSecret()
        {
            var provider = new AWSIAMProvider();
            var mockFactory = new Moq.Mock<IHttpClientFactory>();
            mockFactory.Setup(f => f.CreateClient(Moq.It.IsAny<string>())).Returns(new HttpClient());

            // Act - standalone AccessKeyId without Secret Key
            var result = await provider.ValidateKeyAsync("AKIA1234567890ABCDEF", mockFactory.Object);

            // Assert
            Assert.Equal(ValidationAttemptStatus.ProviderSpecificError, result.Status);
            Assert.Contains("Secret Access Key not found", result.Detail);
        }
    }
}
