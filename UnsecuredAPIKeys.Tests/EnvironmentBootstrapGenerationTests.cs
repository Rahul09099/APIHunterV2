using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 5.1 executable specification for the Master environment-bootstrap generation.
/// These tests intentionally fail until Task 5.2 supplies and wires the bootstrap service.
///
/// **Validates: Requirements 4.1-4.24, 4.46, 17.20, 17.31-17.34**
/// </summary>
public sealed class EnvironmentBootstrapGenerationTests
{
    private const string GitHubMaterialA = "ghp_5a_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string GitHubMaterialB = "ghp_5a_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string GitHubMaterialC = "ghp_5a_CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
    private const string GitLabMaterialA = "glpat-5a-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void WebApiStartup_RegistersMasterOnlyBootstrapBeforeSchedulingReadiness()
    {
        var serviceType = typeof(CredentialStorageService).Assembly.GetType(
            "UnsecuredAPIKeys.Services.EnvironmentBootstrapService",
            throwOnError: false,
            ignoreCase: false);
        Assert.NotNull(serviceType);

        var program = ProviderInstanceTestContract.ReadRepositoryFile(
            "UnsecuredAPIKeys.WebAPI",
            "Program.cs");
        var bootstrapIndex = program.IndexOf("EnvironmentBootstrap", StringComparison.Ordinal);
        var readinessIndex = program.IndexOf(
            "GetRequiredService<ISearchPlatformSchedulingReadinessService>",
            StringComparison.Ordinal);
        var masterGuardIndex = bootstrapIndex < 0
            ? -1
            : program.LastIndexOf("if (!isWorkerMode)", bootstrapIndex, StringComparison.Ordinal);

        Assert.True(bootstrapIndex >= 0, "WebAPI startup must resolve and execute the bootstrap service.");
        Assert.True(
            masterGuardIndex >= 0 && masterGuardIndex < bootstrapIndex,
            "Environment bootstrap must be guarded to the Master role.");
        Assert.True(
            bootstrapIndex < readinessIndex,
            "A successful bootstrap generation must finish before scheduling readiness is evaluated.");
    }

    [Fact]
    public async Task DedicatedInputs_ImportWhileWorkerCompatVariablesAreIgnored()
    {
        // Task 16.3 cuts Worker credential variables away from the fleet: WORKER_*
        // leftovers in deployment configuration must not import any credential.
        await using var host = new BootstrapHost();
        var configuration = BootstrapConfiguration(
            Entry("master-github-primary", "GitHub", GitHubMaterialA),
            Entry("master-gitlab-primary", "GitLab", GitLabMaterialA));
        configuration["WORKER_GITHUB_TOKENS"] = GitHubMaterialB;
        configuration["WORKER_GITLAB_TOKENS"] = GitLabMaterialA;

        await host.RestartAsync(configuration);

        await using var context = host.CreateContext();
        var credentials = await context.SearchProviderTokens
            .AsNoTracking()
            .Where(credential => credential.Source == CredentialSource.Environment)
            .OrderBy(credential => credential.SourceEntryId)
            .ToListAsync();

        Assert.Equal(2, credentials.Count);
        Assert.Collection(
            credentials,
            credential => AssertImported(
                credential,
                "master-github-primary",
                SearchProviderEnum.GitHub),
            credential => AssertImported(
                credential,
                "master-gitlab-primary",
                SearchProviderEnum.GitLab));
        Assert.All(credentials, credential =>
        {
            Assert.NotNull(credential.SourceGeneration);
            Assert.True(credential.SourceGeneration > 0);
            Assert.NotNull(credential.LastSeenUtc);
            Assert.Equal(string.Empty, credential.Token);
            Assert.True(CredentialStorageService.IsProtected(credential));
        });
        Assert.DoesNotContain(
            await context.SearchProviderTokens.AsNoTracking().Select(c => c.SourceEntryId!).ToListAsync(),
            entryId => entryId.StartsWith("compat-worker", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(RejectedGenerationCases))]
    public async Task InvalidGeneration_RejectsEveryEntryWithoutSecretDisclosure(
        string caseName,
        IReadOnlyDictionary<string, string?> configuration,
        string secretCanary)
    {
        await using var host = new BootstrapHost();
        using var response = await host.RestartAsync(configuration);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using (var document = JsonDocument.Parse(body))
        {
            Assert.True(
                document.RootElement.TryGetProperty("bootstrap", out var bootstrap));
            Assert.Equal("Unhealthy", bootstrap.GetString());
        }

        await using var context = host.CreateContext();
        Assert.Empty(await context.SearchProviderTokens
            .AsNoTracking()
            .Where(credential => credential.Source == CredentialSource.Environment)
            .ToListAsync());
        Assert.DoesNotContain(secretCanary, body, StringComparison.Ordinal);
        Assert.DoesNotContain(secretCanary, host.LogText, StringComparison.Ordinal);
        Assert.DoesNotContain(secretCanary, JsonSerializer.Serialize(
            await context.SearchProviderTokens.AsNoTracking().ToListAsync()), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(caseName));
    }

    [Fact]
    public async Task RepeatImport_PreservesIdentityGrantsHealthLeaseAndUsageHistoryWithoutAuthResurrection()
    {
        await using var host = new BootstrapHost();
        var configuration = BootstrapConfiguration(
            Entry("stable-github-slot", "GitHub", GitHubMaterialA));
        await host.RestartAsync(configuration);

        Guid stableId;
        byte[] fingerprint;
        long firstGeneration;
        var disabledAt = Utc(2027, 1, 2, 3, 4, 5);
        var cooldown = Utc(2027, 2, 3, 4, 5, 6);
        var lastClaimed = Utc(2027, 3, 4, 5, 6, 7);
        var lastUsed = Utc(2027, 4, 5, 6, 7, 8);
        var leaseId = Guid.NewGuid();
        var leaseRequestId = Guid.NewGuid();
        var leaseAcquired = Utc(2027, 5, 6, 7, 8, 9);
        var leaseExpires = Utc(2037, 5, 6, 7, 13, 9);

        await using (var context = host.CreateContext())
        {
            var credential = await context.SearchProviderTokens
                .Include(candidate => candidate.CredentialGrants)
                .SingleAsync(candidate => candidate.SourceEntryId == "stable-github-slot");
            stableId = credential.StableId;
            fingerprint = credential.Fingerprint.ToArray();
            firstGeneration = Assert.IsType<long>(credential.SourceGeneration);
            credential.IsEnabled = false;
            credential.DisabledReason = "AuthInvalid";
            credential.DisabledAtUtc = disabledAt;
            credential.CooldownUntilUtc = cooldown;
            credential.ConsecutiveTransientFailures = 4;
            credential.LastOutcome = "AuthInvalid";
            credential.LastClaimedUtc = lastClaimed;
            credential.LastUsedUTC = lastUsed;
            credential.LeaseId = leaseId;
            credential.LeaseOwnerNodeId = "worker-preservation";
            credential.LeaseRequestId = leaseRequestId;
            credential.LeaseAcquiredUtc = leaseAcquired;
            credential.LeaseExpiresUtc = leaseExpires;
            credential.Revision = 17;
            credential.CredentialGrants.Add(new CredentialGrant
            {
                Scope = CredentialGrantScope.User,
                TelegramPrincipalId = 5_100_001,
                CreatedUtc = Utc(2027, 1, 1)
            });
            await context.SaveChangesAsync();
        }

        await host.RestartAsync(configuration);

        await using var verification = host.CreateContext();
        var persisted = await verification.SearchProviderTokens
            .AsNoTracking()
            .Include(candidate => candidate.CredentialGrants)
            .SingleAsync(candidate => candidate.SourceEntryId == "stable-github-slot");
        Assert.Equal(stableId, persisted.StableId);
        Assert.Equal(fingerprint, persisted.Fingerprint);
        Assert.True(persisted.SourceGeneration > firstGeneration);
        Assert.False(persisted.IsEnabled);
        Assert.Equal("AuthInvalid", persisted.DisabledReason);
        Assert.Equal(disabledAt, persisted.DisabledAtUtc);
        Assert.Equal(cooldown, persisted.CooldownUntilUtc);
        Assert.Equal(4, persisted.ConsecutiveTransientFailures);
        Assert.Equal("AuthInvalid", persisted.LastOutcome);
        Assert.Equal(lastClaimed, persisted.LastClaimedUtc);
        Assert.Equal(lastUsed, persisted.LastUsedUTC);
        Assert.Equal(leaseId, persisted.LeaseId);
        Assert.Equal("worker-preservation", persisted.LeaseOwnerNodeId);
        Assert.Equal(leaseRequestId, persisted.LeaseRequestId);
        Assert.Equal(leaseAcquired, persisted.LeaseAcquiredUtc);
        Assert.Equal(leaseExpires, persisted.LeaseExpiresUtc);
        Assert.Equal(17, persisted.Revision);
        var grant = Assert.Single(persisted.CredentialGrants);
        Assert.Equal(CredentialGrantScope.User, grant.Scope);
        Assert.Equal(5_100_001, grant.TelegramPrincipalId);
    }

    [Fact]
    public async Task ChangedMaterialForStableSource_ArchivesOldIdentityAndCreatesProtectedReplacement()
    {
        await using var host = new BootstrapHost();
        await host.RestartAsync(BootstrapConfiguration(
            Entry("rotating-github-slot", "GitHub", GitHubMaterialA)));

        Guid oldStableId;
        byte[] oldFingerprint;
        await using (var context = host.CreateContext())
        {
            var old = await context.SearchProviderTokens
                .SingleAsync(candidate => candidate.SourceEntryId == "rotating-github-slot");
            oldStableId = old.StableId;
            oldFingerprint = old.Fingerprint.ToArray();
        }

        await host.RestartAsync(BootstrapConfiguration(
            Entry("rotating-github-slot", "GitHub", GitHubMaterialC)));

        await using var verification = host.CreateContext();
        var rows = await verification.SearchProviderTokens
            .AsNoTracking()
            .Where(candidate => candidate.SourceEntryId == "rotating-github-slot")
            .OrderBy(candidate => candidate.CreatedUtc)
            .ThenBy(candidate => candidate.Id)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        var oldCredential = rows.Single(candidate => candidate.StableId == oldStableId);
        var replacement = rows.Single(candidate => candidate.StableId != oldStableId);
        Assert.False(oldCredential.IsEnabled);
        Assert.True(oldCredential.IsArchived);
        Assert.NotNull(oldCredential.DisabledAtUtc);
        Assert.Equal("Replaced", oldCredential.DisabledReason);
        Assert.Equal(replacement.StableId, oldCredential.ReplacedByStableId);
        Assert.NotEqual(oldFingerprint, replacement.Fingerprint);
        Assert.True(replacement.IsEnabled);
        Assert.Equal(CredentialSource.Environment, replacement.Source);
        Assert.Equal("rotating-github-slot", replacement.SourceEntryId);
        Assert.NotNull(replacement.SourceGeneration);
        Assert.NotNull(replacement.LastSeenUtc);
        Assert.True(CredentialStorageService.IsProtected(replacement));
        Assert.Equal(string.Empty, replacement.Token);
    }

    [Fact]
    public async Task SuccessfulRemovalGeneration_DisablesOnlyAbsentEnvironmentManagedIdentity()
    {
        await using var host = new BootstrapHost();
        var firstGeneration = BootstrapConfiguration(
            Entry("removed-environment-slot", "GitHub", GitHubMaterialA),
            Entry("manual-provenance-slot", "GitHub", GitHubMaterialB));
        await host.RestartAsync(firstGeneration);

        Guid removedStableId;
        Guid manualStableId;
        await using (var context = host.CreateContext())
        {
            var rows = await context.SearchProviderTokens
                .OrderBy(candidate => candidate.SourceEntryId)
                .ToListAsync();
            removedStableId = rows.Single(candidate =>
                candidate.SourceEntryId == "removed-environment-slot").StableId;
            var manualCredential = rows.Single(candidate =>
                candidate.SourceEntryId == "manual-provenance-slot");
            manualStableId = manualCredential.StableId;
            manualCredential.Source = CredentialSource.Manual;
            await context.SaveChangesAsync();
        }

        await host.RestartAsync(BootstrapConfiguration());

        await using var verification = host.CreateContext();
        var removed = await verification.SearchProviderTokens
            .AsNoTracking()
            .SingleAsync(candidate => candidate.StableId == removedStableId);
        var manual = await verification.SearchProviderTokens
            .AsNoTracking()
            .SingleAsync(candidate => candidate.StableId == manualStableId);
        Assert.False(removed.IsEnabled);
        Assert.False(removed.IsArchived);
        Assert.Equal("RemovedFromEnvironment", removed.DisabledReason);
        Assert.NotNull(removed.DisabledAtUtc);
        Assert.True(manual.IsEnabled);
        Assert.Equal(CredentialSource.Manual, manual.Source);
        Assert.Null(manual.DisabledReason);
        Assert.Null(manual.DisabledAtUtc);
    }

    [Fact]
    public async Task PartiallyInvalidGeneration_RollsBackImportsAndSkipsRemovalReconciliation()
    {
        await using var host = new BootstrapHost();
        await host.RestartAsync(BootstrapConfiguration(
            Entry("retained-slot-a", "GitHub", GitHubMaterialA),
            Entry("retained-slot-b", "GitHub", GitHubMaterialB)));
        var before = await SnapshotAsync(host);
        Assert.Equal(2, before.Length);

        var invalidGeneration = BootstrapConfiguration(
            Entry("retained-slot-a", "GitHub", GitHubMaterialC),
            Entry("malformed-slot", "GitHub", "not-a-provider-credential"));
        using var response = await host.RestartAsync(invalidGeneration);
        var body = await response.Content.ReadAsStringAsync();
        var after = await SnapshotAsync(host);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(before, after);
        Assert.DoesNotContain("not-a-provider-credential", body, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-provider-credential", host.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRequiredKey_RollsBackGenerationAndSkipsRemovalReconciliation()
    {
        await using var host = new BootstrapHost();
        await host.RestartAsync(BootstrapConfiguration(
            Entry("key-retained-slot-a", "GitHub", GitHubMaterialA),
            Entry("key-retained-slot-b", "GitHub", GitHubMaterialB)));
        var before = await SnapshotAsync(host);
        Assert.Equal(2, before.Length);

        var missingKeys = BootstrapConfiguration(
            Entry("key-retained-slot-a", "GitHub", GitHubMaterialC));
        using var response = await host.RestartAsync(missingKeys, includeProtectionKeys: false);
        var after = await SnapshotAsync(host);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task PersistenceFailure_RollsBackGenerationAndSkipsRemovalReconciliation()
    {
        await using var host = new BootstrapHost();
        await host.RestartAsync(BootstrapConfiguration(
            Entry("persistence-retained-a", "GitHub", GitHubMaterialA),
            Entry("persistence-retained-b", "GitHub", GitHubMaterialB)));
        var before = await SnapshotAsync(host);
        Assert.Equal(2, before.Length);

        await host.ExecuteSqlAsync(
            """
            CREATE TRIGGER FailBootstrapCredentialMutation
            BEFORE UPDATE ON SearchProviderTokens
            BEGIN
                SELECT RAISE(ABORT, 'forced bootstrap generation persistence failure');
            END;
            """);

        var failedGeneration = BootstrapConfiguration(
            Entry("persistence-retained-a", "GitHub", GitHubMaterialC));
        using var response = await host.RestartAsync(failedGeneration);
        var after = await SnapshotAsync(host);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(before, after);
        Assert.DoesNotContain(GitHubMaterialC, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubMaterialC, host.LogText, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> RejectedGenerationCases()
    {
        var empty = BootstrapConfiguration(Entry("empty-slot", "GitHub", "   "));
        yield return ["empty", empty, "empty-slot"];

        const string malformed = "malformed-bootstrap-secret-canary";
        var malformedInput = BootstrapConfiguration(Entry("malformed-slot", "GitHub", malformed));
        yield return ["malformed", malformedInput, malformed];

        const string duplicate = "ghp_5a_DUPLICATE_DUPLICATE_DUPLICATE_123456";
        var duplicateInput = BootstrapConfiguration(
            Entry("duplicate-slot-a", "GitHub", duplicate),
            Entry("duplicate-slot-b", "GitHub", duplicate));
        yield return ["duplicate", duplicateInput, duplicate];

        const string unknown = "unknown-provider-secret-canary-123456789";
        var unknownInput = BootstrapConfiguration(Entry("unknown-slot", "UnknownForge", unknown));
        yield return ["unknown-provider", unknownInput, unknown];
    }

    private static BootstrapEntry Entry(string sourceEntryId, string providerKind, string material) =>
        new(sourceEntryId, providerKind, material);

    private static Dictionary<string, string?> BootstrapConfiguration(params BootstrapEntry[] entries)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SearchPlatformFeatureFlagNames.EnvironmentBootstrapEnabled] = "true",
            [SearchPlatformFeatureFlagNames.ProtectedStorageEnabled] = "true",
            [SearchPlatformFeatureFlagNames.InstancesEnabled] = "true",
            [SearchPlatformFeatureFlagNames.SchedulerEnabled] = "true",
            [SearchPlatformFeatureFlagNames.MasterScraperClaimsEnabled] = "false",
            [SearchPlatformFeatureFlagNames.WorkerClaimsEnabled] = "false",
            [SearchPlatformFeatureFlagNames.LegacyTokenSyncEnabled] = "false",
            [SearchPlatformFeatureFlagNames.AllowWorkerLocalFallback] = "false"
        };
        for (var index = 0; index < entries.Length; index++)
        {
            var prefix = $"SearchCredentials:Bootstrap:Entries:{index}";
            values[$"{prefix}:SourceEntryId"] = entries[index].SourceEntryId;
            values[$"{prefix}:ProviderKind"] = entries[index].ProviderKind;
            values[$"{prefix}:Material"] = entries[index].Material;
        }
        return values;
    }

    private static void AssertImported(
        SearchProviderToken credential,
        string sourceEntryId,
        SearchProviderEnum providerKind)
    {
        Assert.NotEqual(Guid.Empty, credential.StableId);
        Assert.Equal(sourceEntryId, credential.SourceEntryId);
        Assert.Equal(providerKind, credential.SearchProvider);
        Assert.True(credential.ProviderInstanceId > 0);
        Assert.Equal(CredentialSource.Environment, credential.Source);
    }

    private static async Task<CredentialSnapshot[]> SnapshotAsync(BootstrapHost host)
    {
        await using var context = host.CreateContext();
        return await context.SearchProviderTokens
            .AsNoTracking()
            .OrderBy(credential => credential.StableId)
            .Select(credential => new CredentialSnapshot(
                credential.StableId,
                credential.Fingerprint,
                credential.Source,
                credential.SourceEntryId,
                credential.SourceGeneration,
                credential.LastSeenUtc,
                credential.IsEnabled,
                credential.DisabledReason,
                credential.DisabledAtUtc,
                credential.IsArchived,
                credential.ReplacedByStableId,
                credential.Revision))
            .ToArrayAsync();
    }

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    private sealed record BootstrapEntry(string SourceEntryId, string ProviderKind, string Material);

    private sealed record CredentialSnapshot(
        Guid StableId,
        byte[] Fingerprint,
        CredentialSource Source,
        string? SourceEntryId,
        long? SourceGeneration,
        DateTime? LastSeenUtc,
        bool IsEnabled,
        string? DisabledReason,
        DateTime? DisabledAtUtc,
        bool IsArchived,
        Guid? ReplacedByStableId,
        long Revision)
    {
        public bool Equals(CredentialSnapshot? other) =>
            other is not null &&
            StableId == other.StableId &&
            Fingerprint.SequenceEqual(other.Fingerprint) &&
            Source == other.Source &&
            SourceEntryId == other.SourceEntryId &&
            SourceGeneration == other.SourceGeneration &&
            LastSeenUtc == other.LastSeenUtc &&
            IsEnabled == other.IsEnabled &&
            DisabledReason == other.DisabledReason &&
            DisabledAtUtc == other.DisabledAtUtc &&
            IsArchived == other.IsArchived &&
            ReplacedByStableId == other.ReplacedByStableId &&
            Revision == other.Revision;

        public override int GetHashCode() => HashCode.Combine(StableId, SourceEntryId, SourceGeneration);
    }

    private sealed class BootstrapHost : IAsyncDisposable
    {
        private readonly SqliteConnection keepAlive;
        private readonly string connectionString;
        private readonly CapturingLoggerProvider loggerProvider = new();
        private BootstrapWebApplicationFactory? factory;

        public BootstrapHost()
        {
            connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"BootstrapTests_{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
                ForeignKeys = true,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString();
            keepAlive = new SqliteConnection(connectionString);
            keepAlive.Open();
        }

        public string LogText => string.Join(Environment.NewLine, loggerProvider.Messages);

        public async Task<HttpResponseMessage> RestartAsync(
            IReadOnlyDictionary<string, string?> configuration,
            bool includeProtectionKeys = true)
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }
            loggerProvider.Clear();
            factory = new BootstrapWebApplicationFactory(
                connectionString,
                configuration,
                includeProtectionKeys,
                loggerProvider);
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            return await client.GetAsync("/health/readiness");
        }

        public DBContext CreateContext() => new(
            new DbContextOptionsBuilder<DBContext>()
                .UseSqlite(connectionString)
                .Options);

        public async Task ExecuteSqlAsync(string sql)
        {
            await using var context = CreateContext();
            await context.Database.ExecuteSqlRawAsync(sql);
        }

        public async ValueTask DisposeAsync()
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }
            await keepAlive.DisposeAsync();
        }
    }

    private sealed class BootstrapWebApplicationFactory(
        string connectionString,
        IReadOnlyDictionary<string, string?> bootstrapConfiguration,
        bool includeProtectionKeys,
        CapturingLoggerProvider loggerProvider)
        : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>(bootstrapConfiguration, StringComparer.Ordinal);
                if (includeProtectionKeys)
                {
                    values["SearchCredentials:Protection:ActiveKeyVersion"] = "7";
                    values["SearchCredentials:Protection:Keys:7"] = Convert.ToBase64String(
                        Enumerable.Repeat((byte)0x37, 32).ToArray());
                    values["SearchCredentials:Fingerprint:ActiveKeyVersion"] = "11";
                    values["SearchCredentials:Fingerprint:Keys:11"] = Convert.ToBase64String(
                        Enumerable.Repeat((byte)0x5B, 32).ToArray());
                }
                configuration.AddInMemoryCollection(values);
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<DBContext>();
                services.RemoveAll<DbContextOptions<DBContext>>();
                services.RemoveAll<IDbContextFactory<DBContext>>();
                services.AddSingleton<ILoggerProvider>(loggerProvider);
                services.AddDbContext<DBContext>(options => options.UseSqlite(connectionString));
                services.AddDbContextFactory<DBContext>(
                    options => options.UseSqlite(connectionString),
                    ServiceLifetime.Scoped);
            });
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> messages = new();

        public IReadOnlyCollection<string> Messages => messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

        public void Clear()
        {
            while (messages.TryDequeue(out _))
            {
            }
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
                if (exception is not null)
                {
                    messages.Enqueue(exception.ToString());
                }
            }
        }
    }
}
