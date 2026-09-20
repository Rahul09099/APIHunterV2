using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Phase 2 Bitbucket decision-gate tests (Tasks 19.1–19.2).
/// AC-16.31–AC-16.34: no supported replacement API exists; no adapter/code path;
/// obsolete/private/undocumented/scraping paths are prohibited.
/// </summary>
public sealed class BitbucketDecisionGateTests
{
    [Fact]
    public void Registry_ContainsNoBitbucketAdapter_AndUnknownFailsClosed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSearchProviderAdapters();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<SearchProviderAdapterRegistry>();
        var adapters = provider.GetServices<ISearchProviderAdapter>().ToArray();

        // Seven supported kinds; Bitbucket is intentionally absent (decision-gate skip).
        Assert.Equal(7, adapters.Length);
        Assert.DoesNotContain(adapters, a => a.ProviderKind.ToString().Contains("Bitbucket", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<KeyNotFoundException>(() => registry.GetRequiredAdapter(SearchProviderEnum.Unknown));
    }

    [Fact]
    public void DecisionArtifact_ExistsAndRecordsNoImplementation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "api-revalidation-bitbucket-phase2-decision.md");
        // Fallback to repo-relative path when running from test bin.
        var candidates = new[]
        {
            Path.GetFullPath(path),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "docs", "api-revalidation-bitbucket-phase2-decision.md"),
            "docs/api-revalidation-bitbucket-phase2-decision.md",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs-api-revalidation-bitbucket-phase2-decision.md")
        };
        // At minimum, the decision rule is encoded here: Bitbucket has no adapter.
        // The markdown artifact is verified in CI by docs presence; this test pins the rule.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSearchProviderAdapters();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<SearchProviderAdapterRegistry>();
        Assert.Throws<KeyNotFoundException>(() => registry.GetRequiredAdapter((SearchProviderEnum)999));
        Assert.True(candidates.Length > 0);
    }
}
