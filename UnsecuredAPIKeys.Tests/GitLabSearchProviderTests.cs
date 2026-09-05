using System.Net;
using System.Text;
using Moq;
using Moq.Protected;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers.Search_Providers;
using Xunit;

namespace UnsecuredAPIKeys.Tests
{
    public class GitLabSearchProviderTests
    {
        [Fact]
        public void ProviderName_IsGitLab()
        {
            var provider = new GitLabSearchProvider();
            Assert.Equal("GitLab", provider.ProviderName);
        }

        [Fact]
        public async Task SearchAsync_ThrowsOnNullToken()
        {
            var provider = new GitLabSearchProvider();
            var query = new SearchQuery { Id = 1, Query = "AKIA" };

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                provider.SearchAsync(query, null, null));
        }

        [Fact]
        public async Task SearchAsync_ParsesGitLabBlobResultsCorrectly()
        {
            var mockJson = """
            [
                {
                    "basename": ".env",
                    "data": "OPENAI_API_KEY=sk-proj-test1234567890",
                    "path": "config/.env",
                    "filename": ".env",
                    "ref": "master",
                    "startline": 1,
                    "project_id": 9999
                }
            ]
            """;

            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(mockJson, Encoding.UTF8, "application/json")
                });

            var httpClient = new HttpClient(handlerMock.Object);
            var provider = new GitLabSearchProvider(httpClient);

            var query = new SearchQuery { Id = 42, Query = "OPENAI_API_KEY" };
            var token = new SearchProviderToken
            {
                Token = "glpat-test-token-12345",
                SearchProvider = SearchProviderEnum.GitLab,
                IsEnabled = true
            };

            var response = await provider.SearchAsync(query, token, null, 1);

            Assert.NotNull(response);
            Assert.Single(response.Results);

            var item = response.Results.First();
            Assert.Equal(42, item.SearchQueryId);
            Assert.Equal("GitLab", item.Provider);
            Assert.Equal(9999, item.RepoId);
            Assert.Equal("config/.env", item.FilePath);
            Assert.Equal(".env", item.FileName);
            Assert.Equal("master", item.Branch);
            Assert.Contains("projects/9999", item.RepoURL);
            Assert.Contains("projects/9999/repository/files/config%2F.env/raw", item.ApiContentUrl);
        }
    }
}
