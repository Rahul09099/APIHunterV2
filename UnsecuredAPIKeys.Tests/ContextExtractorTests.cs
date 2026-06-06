using System.Threading.Tasks;
using Xunit;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers.ServerProviders.Services;

namespace UnsecuredAPIKeys.Tests
{
    public class ContextExtractorTests
    {
        [Fact]
        public async Task ExtractContextAsync_ReturnsCorrectLines()
        {
            // Arrange
            var extractor = new ContextExtractor();
            var lines = new[]
            {
                "line0", "line1", "line2", "line3", "line4",
                "line5", "line6", "line7", "line8", "line9",
                "line10", "line11", "line12", "line13", "line14"
            };
            var fileContent = string.Join("\n", lines);
            // "line5" is the match. Let's find its position
            int position = fileContent.IndexOf("line5");

            // Act
            var context = await extractor.ExtractContextAsync(fileContent, position, contextLines: 3);

            // Assert
            Assert.Equal(5, context.MatchLine);
            Assert.Equal(2, context.StartLine);
            Assert.Equal(8, context.EndLine);
            Assert.Contains("line2", context.FullContext);
            Assert.Contains("line5", context.FullContext);
            Assert.Contains("line8", context.FullContext);
            Assert.DoesNotContain("line1", context.FullContext);
            Assert.DoesNotContain("line9", context.FullContext);
        }

        [Fact]
        public async Task ExtractContextAsync_BoundaryAtStart_WorksCorrectly()
        {
            var extractor = new ContextExtractor();
            var lines = new[] { "line0", "line1", "line2", "line3" };
            var fileContent = string.Join("\n", lines);
            int position = fileContent.IndexOf("line0");

            var context = await extractor.ExtractContextAsync(fileContent, position, contextLines: 2);

            Assert.Equal(0, context.MatchLine);
            Assert.Equal(0, context.StartLine);
            Assert.Equal(2, context.EndLine);
        }

        [Fact]
        public async Task ExtractContextAsync_BoundaryAtEnd_WorksCorrectly()
        {
            var extractor = new ContextExtractor();
            var lines = new[] { "line0", "line1", "line2", "line3" };
            var fileContent = string.Join("\n", lines);
            int position = fileContent.IndexOf("line3");

            var context = await extractor.ExtractContextAsync(fileContent, position, contextLines: 2);

            Assert.Equal(3, context.MatchLine);
            Assert.Equal(1, context.StartLine);
            Assert.Equal(3, context.EndLine);
        }

        [Theory]
        [InlineData("password = mysecretpassword", "admin", "mysecretpassword")]
        [InlineData("pass: SuperSecret", "admin", "SuperSecret")]
        [InlineData("pwd='AnotherSecretPassword';", "user", "AnotherSecretPassword")]
        [InlineData("admin = 12345", "admin", "12345")]
        public void FindRelatedPassword_MatchesCommonPatterns(string context, string username, string expectedPassword)
        {
            var extractor = new ContextExtractor();
            var password = extractor.FindRelatedPassword(context, username);
            Assert.Equal(expectedPassword, password);
        }

        [Theory]
        [InlineData("host: 192.168.1.1", "192.168.1.1")]
        [InlineData("server='myhost.domain.com';", "myhost.domain.com")]
        [InlineData("10.0.0.5", "10.0.0.5")]
        public void FindRelatedHost_ExtractsCorrectly(string context, string expectedHost)
        {
            var extractor = new ContextExtractor();
            var host = extractor.FindRelatedHost(context);
            Assert.Equal(expectedHost, host);
        }

        [Fact]
        public void FindRelatedPort_ExtractsPortOrReturnsDefault()
        {
            var extractor = new ContextExtractor();
            
            // Port pattern match
            var port1 = extractor.FindRelatedPort("port: 2222", CredentialType.SSH);
            Assert.Equal(2222, port1);

            // Fallback to default
            var port2 = extractor.FindRelatedPort("no port specified here", CredentialType.SSH);
            Assert.Equal(22, port2);
        }
    }
}
