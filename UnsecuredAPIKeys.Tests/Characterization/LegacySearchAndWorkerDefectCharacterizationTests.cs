using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.DTOs;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers.Search_Providers;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests.Characterization;

/// <summary>
/// Locks in known pre-Phase-0 defects. These assertions intentionally describe legacy behavior,
/// not the desired multi-provider scheduling contract, so later tasks can replace them explicitly.
/// </summary>
[Trait("Category", "LegacyDefectCharacterization")]
[Trait("Requirement", "AC-17.22")]
public sealed class LegacySearchAndWorkerDefectCharacterizationTests
{
    [Fact]
    public async Task ScraperService_AfterSuccessfulSearches_KeepsSelectingTheFirstToken()
    {
        var handler = new RecordingGitLabHandler(HttpStatusCode.OK, "[]");
        using var httpClient = new HttpClient(handler);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DBContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new DBContext(options);
        await db.Database.EnsureCreatedAsync();

        var (storage, materialAccess) = CreateCredentialServices(db);
        var instance = CreateGitLabInstance();
        db.SearchProviderInstances.Add(instance);
        await db.SaveChangesAsync();
        var first = storage.CreateProtectedCredential(
            "glpat-first-characterization-token",
            instance);
        var second = storage.CreateProtectedCredential(
            "glpat-second-characterization-token",
            instance);
        db.SearchProviderTokens.AddRange(first, second);
        await db.SaveChangesAsync();

        var scraper = CreateScraper(db, options, httpClient, materialAccess);
        scraper.IsWorkerMode = true;
        using var cancellation = new CancellationTokenSource();
        SetLegacyCancellationSource(scraper, cancellation);

        var credentials = new List<CredentialOperationReference>
        {
            CreateReference(first, instance),
            CreateReference(second, instance)
        };
        var query = new SearchQuery { Id = 1, Query = "CHARACTERIZATION_QUERY", IsEnabled = true };
        var cursor = CreateLegacyTokenCursor();

        await InvokeLegacyScrapingCycleAsync(scraper, credentials, query, cursor);
        await InvokeLegacyScrapingCycleAsync(scraper, credentials, query, cursor);

        Assert.Equal(
            new[] { "glpat-first-characterization-token", "glpat-first-characterization-token" },
            handler.ObservedPrivateTokens);
        Assert.Equal(0, ReadLegacyTokenCursorIndex(cursor));
    }

    [Fact]
    public async Task ScraperService_AfterSuccessfulSearch_DoesNotPersistTokenLastUsedUtc()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DBContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new DBContext(options);
        await db.Database.EnsureCreatedAsync();

        var (storage, materialAccess) = CreateCredentialServices(db);
        var instance = CreateGitLabInstance();
        db.SearchProviderInstances.Add(instance);
        await db.SaveChangesAsync();
        var token = storage.CreateProtectedCredential(
            "glpat-last-used-characterization-token",
            instance);
        var query = new SearchQuery { Query = "CHARACTERIZATION_QUERY", IsEnabled = true };
        db.SearchProviderTokens.Add(token);
        db.SearchQueries.Add(query);
        await db.SaveChangesAsync();

        var handler = new RecordingGitLabHandler(HttpStatusCode.OK, "[]");
        using var httpClient = new HttpClient(handler);
        var scraper = CreateScraper(db, options, httpClient, materialAccess);
        using var cancellation = new CancellationTokenSource();
        SetLegacyCancellationSource(scraper, cancellation);

        await InvokeLegacyScrapingCycleAsync(
            scraper,
            new List<CredentialOperationReference> { CreateReference(token, instance) },
            query,
            CreateLegacyTokenCursor());

        db.ChangeTracker.Clear();
        var persistedToken = await db.SearchProviderTokens.SingleAsync();
        var persistedQuery = await db.SearchQueries.SingleAsync();

        Assert.NotEqual(default, persistedQuery.LastSearchUTC);
        Assert.Null(persistedToken.LastUsedUTC);
    }

    [Fact]
    public void GitHubSearchProvider_ApiFailurePath_BreaksAndReturnsAResponseInsteadOfPropagating()
    {
        // GitHubSearchProvider constructs its transport internally, so this pre-refactor
        // characterization records the failure branch directly without making a live API call.
        var source = ReadRepositoryFile(
            "UnsecuredAPIKeys.Providers",
            "Search Providers",
            "GitHubSearchProvider.cs");

        var apiCatchStart = source.IndexOf("catch (ApiException apiEx)", StringComparison.Ordinal);
        var outerCatchStart = source.IndexOf("catch (Exception ex)", apiCatchStart, StringComparison.Ordinal);
        var responseReturn = source.IndexOf("return new SearchResponse", outerCatchStart, StringComparison.Ordinal);

        Assert.True(apiCatchStart >= 0, "Expected the legacy GitHub ApiException branch.");
        Assert.True(outerCatchStart > apiCatchStart, "Expected the legacy outer catch after the API branch.");
        Assert.Contains("break;", source[apiCatchStart..outerCatchStart]);
        Assert.True(responseReturn > outerCatchStart, "Expected a SearchResponse after the swallowed failure.");
    }

    [Fact]
    public async Task GitLabSearchProvider_WhenRateLimited_PropagatesHttp429()
    {
        var handler = new RecordingGitLabHandler(HttpStatusCode.TooManyRequests, "rate limited");
        using var httpClient = new HttpClient(handler);
        var provider = new GitLabSearchProvider(httpClient);
        var query = new SearchQuery { Id = 42, Query = "CHARACTERIZATION_QUERY" };
        var token = CreateGitLabToken("glpat-rate-limit-characterization-token");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.SearchAsync(query, token, null));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
    }

    [Fact]
    public void NodesController_Sync_IsCredentialFree()
    {
        var source = CompactWhitespace(ReadRepositoryFile(
            "UnsecuredAPIKeys.WebAPI",
            "Controllers",
            "NodesController.cs"));

        Assert.DoesNotContain("SearchProviderTokens", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Tokens =", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(NodeSyncDTO).GetProperties(),
            property => property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase));

        var serialized = JsonSerializer.Serialize(new NodeSyncDTO
        {
            Queries = [new SearchQueryDTO { Id = 1, Query = "safe-sync-query" }]
        });
        Assert.DoesNotContain("Token", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Credential", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NodesController_Report_PersistsActualProvenanceAfterValidation()
    {
        // Task 13.3 replaces the hardcoded-GitHub defect: discoveries carry immutable
        // normalized provenance that the Master validates before persistence.
        var source = ReadRepositoryFile(
            "UnsecuredAPIKeys.WebAPI",
            "Controllers",
            "NodesController.cs");

        Assert.DoesNotContain("Provider = \"GitHub (Ghost)\"", source, StringComparison.Ordinal);
        Assert.Contains("SearchProvider = discovery.ProviderKind", source, StringComparison.Ordinal);
        Assert.Contains("(Ghost)", source, StringComparison.Ordinal);
        Assert.Contains("_reportValidator.ValidateAsync", source, StringComparison.Ordinal);
        Assert.Contains(
            typeof(NodeReportDto).GetProperties(),
            property => property.Name.Contains("Provider", StringComparison.Ordinal));
    }

    [Fact]
    public void WebApiWorkerRole_RegistersKeepAliveAndScraperHostedServiceOnlyInWorkerMode()
    {
        // Task 13.2 replaces the missing-Worker-startup defect: the worker branch owns
        // monitoring (KeepAlive) and orchestration (WorkerScraperHostedService); the
        // master branch registers neither.
        var source = ReadRepositoryFile("UnsecuredAPIKeys.WebAPI", "Program.cs");
        var workerBranchStart = source.IndexOf("if (isWorkerMode)", StringComparison.Ordinal);
        var masterBranchStart = source.IndexOf("\nelse", workerBranchStart, StringComparison.Ordinal);

        Assert.True(workerBranchStart >= 0, "Expected the WebAPI worker-mode registration branch.");
        Assert.True(masterBranchStart > workerBranchStart, "Expected the WebAPI master-mode branch.");

        var workerBranch = source[workerBranchStart..masterBranchStart];
        var masterBranch = source[masterBranchStart..];
        Assert.Contains("AddHostedService<NodeKeepAliveService>()", workerBranch);
        Assert.Contains("AddHostedService<WorkerScraperHostedService>()", workerBranch);
        Assert.DoesNotContain("WorkerScraperHostedService", masterBranch);
    }

    private static ScraperService CreateScraper(
        DBContext db,
        DbContextOptions<DBContext> options,
        HttpClient httpClient,
        CredentialMaterialAccessService materialAccessService)
    {
        return new ScraperService(
            db,
            new TestDbContextFactory(options),
            new StaticHttpClientFactory(httpClient),
            materialAccessService: materialAccessService);
    }

    private static SearchProviderToken CreateGitLabToken(string value)
    {
        return new SearchProviderToken
        {
            Token = value,
            SearchProvider = SearchProviderEnum.GitLab,
            IsEnabled = true
        };
    }

    private static (CredentialStorageService Storage, CredentialMaterialAccessService MaterialAccess)
        CreateCredentialServices(DBContext db)
    {
        var protection = new CredentialProtectionService(
            new Dictionary<int, byte[]> { [1] = Enumerable.Repeat((byte)0x11, 32).ToArray() },
            activeVersion: 1);
        var fingerprint = new CredentialFingerprintService(
            new Dictionary<int, byte[]> { [1] = Enumerable.Repeat((byte)0x22, 32).ToArray() },
            activeVersion: 1);
        var guard = new CredentialStorageMigrationGuard(
            new ConfigurationBuilder().Build());
        return (
            new CredentialStorageService(protection, fingerprint, guard),
            new CredentialMaterialAccessService(db, protection, guard));
    }

    private static SearchProviderInstance CreateGitLabInstance() => new()
    {
        StableId = ProviderInstanceSchema.DefaultGitLabStableId,
        ProviderKind = SearchProviderEnum.GitLab,
        DisplayName = "GitLab",
        NormalizedScheme = "https",
        NormalizedHost = "gitlab.com",
        NormalizedPort = 443,
        NormalizedBasePath = "/api/v4",
        IsEnabled = true,
        AllowGlobalPublicSearch = false,
        MaxConcurrentOperations = 4,
        SettingsVersion = 1,
        SettingsJson = "{}",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static CredentialOperationReference CreateReference(
        SearchProviderToken credential,
        SearchProviderInstance instance) => new(
            credential.StableId,
            credential.SearchProvider,
            instance.StableId,
            credential.Revision,
            credential.LeaseId);

    private static object CreateLegacyTokenCursor()
    {
        var cursorType = typeof(ScraperService).GetNestedType("TokenCursor", BindingFlags.NonPublic);
        Assert.NotNull(cursorType);
        return Activator.CreateInstance(cursorType!, nonPublic: true)!;
    }

    private static int ReadLegacyTokenCursorIndex(object cursor)
    {
        var indexProperty = cursor.GetType().GetProperty("Index", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(indexProperty);
        return Assert.IsType<int>(indexProperty!.GetValue(cursor));
    }

    private static async Task InvokeLegacyScrapingCycleAsync(
        ScraperService scraper,
        IReadOnlyList<CredentialOperationReference> credentials,
        SearchQuery query,
        object cursor)
    {
        var method = typeof(ScraperService).GetMethod(
            "RunScrapingCycleUtilsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var invocation = method!.Invoke(scraper, new object?[] { credentials, query, cursor, null, null, 1 });
        var task = Assert.IsAssignableFrom<Task>(invocation);
        await task;
    }

    private static void SetLegacyCancellationSource(
        ScraperService scraper,
        CancellationTokenSource cancellation)
    {
        var field = typeof(ScraperService).GetField(
            "_cancellationTokenSource",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(scraper, cancellation);
    }

    private static string ReadRepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !File.Exists(Path.Combine(directory.FullName, "UnsecuredAPIKeys-OpenSource.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = directory!.FullName;
        foreach (var segment in relativeSegments)
        {
            path = Path.Combine(path, segment);
        }

        Assert.True(File.Exists(path), $"Expected repository file '{path}'.");
        return File.ReadAllText(path);
    }

    private static string CompactWhitespace(string value) => Regex.Replace(value, "\\s+", " ");

    private sealed class TestDbContextFactory(DbContextOptions<DBContext> options)
        : IDbContextFactory<DBContext>
    {
        public DBContext CreateDbContext() => new(options);

        public Task<DBContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingGitLabHandler(HttpStatusCode statusCode, string body)
        : HttpMessageHandler
    {
        public List<string> ObservedPrivateTokens { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues("PRIVATE-TOKEN", out var tokenValues))
            {
                ObservedPrivateTokens.Add(tokenValues.Single());
            }

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            if (statusCode == HttpStatusCode.TooManyRequests)
            {
                response.Headers.TryAddWithoutValidation(
                    "RateLimit-Reset",
                    DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds().ToString());
            }

            return Task.FromResult(response);
        }
    }
}
