using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Moq.Protected;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers.Communication_Providers;
using UnsecuredAPIKeys.Providers.Common;
using Xunit;

namespace UnsecuredAPIKeys.Tests
{
    public class GitHubTokenProviderTests
    {
        [Theory]
        [InlineData("ghp_1234567890abcdefghijklmnopqrstuvwxyz")]
        [InlineData("github_pat_11AAAAAAA0123456789ABC_1234567890abcdefghijklmnopqrstuvwxyz1234567890abcdefghijklm")]
        [InlineData("gho_1234567890abcdefghijklmnopqrstuvwxyz")]
        [InlineData("ghu_1234567890abcdefghijklmnopqrstuvwxyz")]
        [InlineData("ghs_1234567890abcdefghijklmnopqrstuvwxyz")]
        [InlineData("ghr_1234567890abcdefghijklmnopqrstuvwxyz")]
        [InlineData("GITHUB_TOKEN=ghp_1234567890abcdefghijklmnopqrstuvwxyz")]
        [InlineData("GH_TOKEN=ghp_1234567890abcdefghijklmnopqrstuvwxyz")]
        [InlineData("GITHUB_PAT=github_pat_11AAAAAAA0123456789ABC_1234567890abcdefghijklmnopqrstuvwxyz")]
        public void RegexPatterns_MatchValidGitHubTokens(string token)
        {
            var provider = new GitHubTokenProvider();
            var matches = false;
            foreach (var pattern in provider.RegexPatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(token, pattern))
                {
                    matches = true;
                    break;
                }
            }

            Assert.True(matches, $"Token '{token}' should match GitHub provider regex patterns.");
        }

        [Fact]
        public async Task ValidateKeyAsync_ClassicToken_ReturnsSuccessWithScopes()
        {
            // Arrange
            var mockHandler = new Mock<HttpMessageHandler>();
            var responseJson = @"{
                ""login"": ""testuser"",
                ""id"": 123456,
                ""type"": ""User"",
                ""name"": ""Test User"",
                ""public_repos"": 15,
                ""plan"": {
                    ""name"": ""pro""
                }
            }";

            var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            };
            httpResponse.Headers.Add("X-OAuth-Scopes", "repo, user");

            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(httpResponse);

            var httpClient = new HttpClient(mockHandler.Object);
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            var provider = new GitHubTokenProvider();

            // Act
            var result = await provider.ValidateKeyAsync("ghp_1234567890abcdefghijklmnopqrstuvwxyz", mockFactory.Object);

            // Assert
            Assert.Equal(ValidationAttemptStatus.Valid, result.Status);
            Assert.Contains("Classic PAT", result.AccountTier);
            Assert.Contains("testuser", result.AccountTier);
            Assert.Contains("Classic Scopes: repo, user", result.AccountTier);
        }

        [Fact]
        public async Task ValidateKeyAsync_FineGrainedToken_ReturnsFineGrainedNotice()
        {
            // Arrange
            var mockHandler = new Mock<HttpMessageHandler>();
            var responseJson = @"{
                ""login"": ""fguser"",
                ""id"": 789012,
                ""type"": ""User""
            }";

            var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            };

            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(httpResponse);

            var httpClient = new HttpClient(mockHandler.Object);
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            var provider = new GitHubTokenProvider();

            // Act
            var result = await provider.ValidateKeyAsync("github_pat_1234567890abcdefghijklmnopqrstuvwxyz", mockFactory.Object);

            // Assert
            Assert.Equal(ValidationAttemptStatus.Valid, result.Status);
            Assert.Contains("Fine-Grained PAT", result.AccountTier);
            Assert.Contains("Fine-grained Permissions (Not exposed by /user)", result.AccountTier);
        }

        [Fact]
        public async Task ValidateKeyAsync_401Unauthorized_ReturnsUnauthorizedResult()
        {
            // Arrange
            var mockHandler = new Mock<HttpMessageHandler>();
            var httpResponse = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(@"{""message"":""Bad credentials""}")
            };

            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(httpResponse);

            var httpClient = new HttpClient(mockHandler.Object);
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            var provider = new GitHubTokenProvider();

            // Act
            var result = await provider.ValidateKeyAsync("ghp_invalidtoken123456789012345678901234", mockFactory.Object);

            // Assert
            Assert.Equal(ValidationAttemptStatus.Unauthorized, result.Status);
            Assert.Contains("Invalid or revoked", result.Detail);
        }

        [Fact]
        public async Task ValidateKeyAsync_500ServerError_ReturnsValidationUnavailable()
        {
            // Arrange
            var mockHandler = new Mock<HttpMessageHandler>();
            var httpResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);

            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(httpResponse);

            var httpClient = new HttpClient(mockHandler.Object);
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            var provider = new GitHubTokenProvider();

            // Act
            var result = await provider.ValidateKeyAsync("ghp_validtoken123456789012345678901234", mockFactory.Object);

            // Assert
            Assert.Equal(ValidationAttemptStatus.ValidationUnavailable, result.Status);
        }
    }
}
