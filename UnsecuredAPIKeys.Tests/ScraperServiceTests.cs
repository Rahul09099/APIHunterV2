using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;

namespace UnsecuredAPIKeys.Tests
{
    public class ScraperServiceTests
    {
        private class TestDbContextFactory : IDbContextFactory<DBContext>
        {
            private readonly DbContextOptions<DBContext> _options;

            public TestDbContextFactory(DbContextOptions<DBContext> options)
            {
                _options = options;
            }

            public DBContext CreateDbContext() => new DBContext(_options);
            public Task<DBContext> CreateDbContextAsync(CancellationToken cancellationToken = default) 
                => Task.FromResult(new DBContext(_options));
        }

        private static DbContextOptions<DBContext> CreateNewContextOptions()
        {
            return new DbContextOptionsBuilder<DBContext>()
                .UseSqlite($"Data Source=InMemoryScraperDb_{Guid.NewGuid():N};Mode=Memory;Cache=Shared")
                .Options;
        }

        [Fact]
        public async Task RunScrapeByGroupAsync_WhenCancelled_HandlesCancellationGracefully()
        {
            var options = CreateNewContextOptions();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                options.FindExtension<Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal.SqliteOptionsExtension>()?.ConnectionString);
            await connection.OpenAsync();

            using var db = new DBContext(options);
            await db.Database.EnsureCreatedAsync();

            // Seed a GitHub token
            db.SearchProviderTokens.Add(new SearchProviderToken
            {
                Token = "ghp_mock_token_for_cancellation_test",
                SearchProvider = SearchProviderEnum.GitHub,
                IsEnabled = true
            });

            // Seed an AWS IAM search query
            db.SearchQueries.Add(new SearchQuery
            {
                Query = "AKIA[0-9A-Z]{16}",
                IsEnabled = true
            });

            await db.SaveChangesAsync();

            var factory = new TestDbContextFactory(options);
            var mockHttpClientFactory = new Mock<IHttpClientFactory>();
            mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());

            var scraper = new ScraperService(db, factory, mockHttpClientFactory.Object);

            // Pre-cancel the token
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert: Should complete gracefully without throwing unhandled exceptions
            var exception = await Record.ExceptionAsync(() => 
                scraper.RunScrapeByGroupAsync("AWS IAM", false, 123456789, cts.Token));

            Assert.Null(exception);
        }

        [Fact]
        public async Task RunScrapeAllGroupsAsync_WhenCancelled_HandlesCancellationGracefully()
        {
            var options = CreateNewContextOptions();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                options.FindExtension<Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal.SqliteOptionsExtension>()?.ConnectionString);
            await connection.OpenAsync();

            using var db = new DBContext(options);
            await db.Database.EnsureCreatedAsync();

            // Seed a GitHub token
            db.SearchProviderTokens.Add(new SearchProviderToken
            {
                Token = "ghp_mock_token_for_all_groups_cancellation_test",
                SearchProvider = SearchProviderEnum.GitHub,
                IsEnabled = true
            });

            // Seed queries
            db.SearchQueries.Add(new SearchQuery { Query = "AKIA[0-9A-Z]{16}", IsEnabled = true });
            db.SearchQueries.Add(new SearchQuery { Query = "sk-ant-api03-[A-Za-z0-9_-]{95}", IsEnabled = true });
            await db.SaveChangesAsync();

            var factory = new TestDbContextFactory(options);
            var mockHttpClientFactory = new Mock<IHttpClientFactory>();
            mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());

            var scraper = new ScraperService(db, factory, mockHttpClientFactory.Object);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var exception = await Record.ExceptionAsync(() => 
                scraper.RunScrapeAllGroupsAsync(123456789, cts.Token));

            Assert.Null(exception);
        }
    }
}
