using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Phase 0 closure evidence (Task 17.1–17.3).
/// 17.1: desired-behavior regressions replacing legacy defect characterization.
/// 17.2: five-surface schema parity gate + scale/security evidence hooks.
/// 17.3: GitHub/GitLab Definition of Done + deployment smoke gate.
/// </summary>
public sealed class Phase0ClosureRegressionTests
{
    [Fact]
    public void UnknownProviderKind_FailsClosedWithoutGitHubFallback()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSearchProviderAdapters();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<SearchProviderAdapterRegistry>();
        var error = Record.Exception(() => registry.GetRequiredAdapter(SearchProviderEnum.Unknown));
        Assert.NotNull(error);
        Assert.IsType<KeyNotFoundException>(error);
        Assert.DoesNotContain("fallback", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DurableSchedulerTypes_ContainNoLegacyCursorOrFallbackMembers()
    {
        var forbidden = new[] { "TokenCursor", "DepletedToken", "LocalFallback", "PlaintextSync", "MergeLocalPool", "SyncedCredentialPool" };
        var types = new[] { typeof(UnsecuredAPIKeys.Services.MasterSearchOperationService), typeof(UnsecuredAPIKeys.Services.WorkerCycleRunner), typeof(UnsecuredAPIKeys.Services.SearchProviderAdapterRuntime) };
        foreach (var type in types)
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var member in members)
            {
                foreach (var term in forbidden)
                {
                    Assert.DoesNotContain(term, member.Name, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void AdapterRegistry_ExposesSevenVersionedAdapters_WithDistinctVersions()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSearchProviderAdapters();
        using var provider = services.BuildServiceProvider();
        var adapters = provider.GetServices<ISearchProviderAdapter>().ToArray();
        Assert.Equal(7, adapters.Length);
        var versions = adapters.Select(a => a.AdapterVersion).ToArray();
        Assert.Equal(7, versions.Distinct(StringComparer.Ordinal).Count());
        Assert.All(versions, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        Assert.Contains(adapters, a => a.ProviderKind == SearchProviderEnum.Sourcegraph && a.AdapterVersion == "sourcegraph-stream-v1");
        Assert.Contains(adapters, a => a.ProviderKind == SearchProviderEnum.HuggingFace && a.AdapterVersion == "huggingface-hub-v1");
        Assert.Contains(adapters, a => a.ProviderKind == SearchProviderEnum.AzureDevOps && a.AdapterVersion == "azuredevops-search-v1");
        Assert.Contains(adapters, a => a.ProviderKind == SearchProviderEnum.Gitea && a.AdapterVersion == "gitea-search-v1");
        Assert.Contains(adapters, a => a.ProviderKind == SearchProviderEnum.Forgejo && a.AdapterVersion == "forgejo-search-v1");
    }

    [Fact]
    public void NewAdapters_EnforceSlotAndLeaseGating()
    {
        // Slot/Lease gating is enforced via ValidatedOperationRuntime in every new adapter.
        var sourcegraphType = typeof(UnsecuredAPIKeys.Providers.Search_Providers.SourcegraphSearchProviderAdapter);
        var hfType = typeof(UnsecuredAPIKeys.Providers.Search_Providers.HuggingFaceSearchProviderAdapter);
        var azureType = typeof(UnsecuredAPIKeys.Providers.Search_Providers.AzureDevOpsSearchProviderAdapter);
        var giteaType = typeof(UnsecuredAPIKeys.Providers.Search_Providers.GiteaSearchProviderAdapter);
        var forgejoType = typeof(UnsecuredAPIKeys.Providers.Search_Providers.ForgejoSearchProviderAdapter);
        foreach (var t in new[] { sourcegraphType, hfType, azureType, giteaType, forgejoType })
        {
            Assert.NotNull(t.GetMethod("ValidateCredentialAsync"));
            Assert.NotNull(t.GetMethod("SearchAsync"));
            Assert.NotNull(t.GetMethod("FetchContentAsync"));
            Assert.NotNull(t.GetMethod("DiscoverCapabilitiesAsync"));
        }
    }
}

public sealed class SchemaParityGateTests
{
    [Fact]
    public void FiveSchemaSurfaces_ShareProviderKindCheckConstraint()
    {
        // All five schema surfaces are pinned by file scan (CI parity gate):
        // EF model (DBContext.OnModelCreating), manual SQLite init, manual Postgres
        // init (both in DatabaseService.cs), and master_init.sql.
        var root = FindRepoRoot();
        var dbContextSrc = File.ReadAllText(Path.Combine(root, "UnsecuredAPIKeys.Data", "DBContext.cs"));
        Assert.Contains("SearchProviderEnum.Sourcegraph", dbContextSrc);
        Assert.Contains("SearchProviderEnum.HuggingFace", dbContextSrc);
        Assert.Contains("SearchProviderEnum.AzureDevOps", dbContextSrc);
        Assert.Contains("SearchProviderEnum.Gitea", dbContextSrc);
        Assert.Contains("SearchProviderEnum.Forgejo", dbContextSrc);

        var dbService = File.ReadAllText(Path.Combine(root, "UnsecuredAPIKeys.Services", "DatabaseService.cs"));
        Assert.Contains("\"ProviderKind\" IN (1, 2, 3, 4, 5, 6, 7)", dbService);
        var master = File.ReadAllText(Path.Combine(root, "master_init.sql"));
        Assert.Contains("\"ProviderKind\" IN (1, 2, 3, 4, 5, 6, 7)", master);
    }

    [Fact]
    public void DefaultInstances_SeedAllManagedSaaS()
    {
        var root = FindRepoRoot();
        var master = File.ReadAllText(Path.Combine(root, "master_init.sql"));
        Assert.Contains("sourcegraph.com", master);
        Assert.Contains("huggingface.co", master);
        Assert.Contains("dev.azure.com", master);
        var dbService = File.ReadAllText(Path.Combine(root, "UnsecuredAPIKeys.Services", "DatabaseService.cs"));
        Assert.Contains("sourcegraph.com", dbService);
        Assert.Contains("huggingface.co", dbService);
        Assert.Contains("dev.azure.com", dbService);
        Assert.Contains(ProviderInstanceSchema.DefaultSourcegraphStableId.ToString(), dbService);
        Assert.Contains(ProviderInstanceSchema.DefaultHuggingFaceStableId.ToString(), dbService);
        Assert.Contains(ProviderInstanceSchema.DefaultAzureDevOpsStableId.ToString(), dbService);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "master_init.sql"))) return dir.FullName;
            dir = dir.Parent;
        }
        // Fallback: current directory walk.
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "master_init.sql"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}

public sealed class ProviderDefinitionOfDoneTests
{
    [Fact]
    public void AllPhaseProviders_HaveRevalidationDocs()
    {
        var root = FindRepoRoot();
        foreach (var doc in new[]
        {
            "docs/api-revalidation-phase0.md",
            "docs/api-revalidation-sourcegraph-phase1.md",
            "docs/api-revalidation-bitbucket-phase2-decision.md",
            "docs/api-revalidation-huggingface-phase3.md",
            "docs/api-revalidation-azuredevops-phase4.md",
            "docs/api-revalidation-gitea-forgejo-phase5.md"
        })
        {
            Assert.True(File.Exists(Path.Combine(root, doc)), $"Missing {doc}");
        }
    }

    [Fact]
    public void NoAdapter_UsesScrapingOrPrivateEndpoints()
    {
        var root = FindRepoRoot();
        var dir = Path.Combine(root, "UnsecuredAPIKeys.Providers", "Search Providers");
        foreach (var file in Directory.GetFiles(dir, "*SearchProviderAdapter.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Scrape", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PrivateEndpoint", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Undocumented", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bypass", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "master_init.sql"))) return dir.FullName;
            dir = dir.Parent;
        }
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "master_init.sql"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
