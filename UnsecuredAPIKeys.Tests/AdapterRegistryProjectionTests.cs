using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Tests.Infrastructure;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

public sealed class AdapterRegistryRegistrationTests
{
    /// <summary>
    /// **Validates: Requirements 8.1-8.3, 8.25**
    /// </summary>
    [Fact]
    public void DefaultRegistration_ProvidesExactlyOneMetadataAdapterPerSupportedKind()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSearchProviderAdapters();

        using var provider = services.BuildServiceProvider();
        var adapters = provider.GetServices<ISearchProviderAdapter>().ToArray();
        var registry = provider.GetRequiredService<SearchProviderAdapterRegistry>();

        Assert.Equal(7, adapters.Length);
        Assert.Equal(7, adapters.Select(adapter => adapter.ProviderKind).Distinct().Count());
        Assert.Equal(SearchProviderEnum.GitHub, registry.GetRequiredAdapter(SearchProviderEnum.GitHub).ProviderKind);
        Assert.Equal(SearchProviderEnum.GitLab, registry.GetRequiredAdapter(SearchProviderEnum.GitLab).ProviderKind);
        Assert.Equal(SearchProviderEnum.Sourcegraph, registry.GetRequiredAdapter(SearchProviderEnum.Sourcegraph).ProviderKind);
        Assert.Equal(SearchProviderEnum.HuggingFace, registry.GetRequiredAdapter(SearchProviderEnum.HuggingFace).ProviderKind);
        Assert.Equal(SearchProviderEnum.AzureDevOps, registry.GetRequiredAdapter(SearchProviderEnum.AzureDevOps).ProviderKind);
        Assert.Equal(SearchProviderEnum.Gitea, registry.GetRequiredAdapter(SearchProviderEnum.Gitea).ProviderKind);
        Assert.Equal(SearchProviderEnum.Forgejo, registry.GetRequiredAdapter(SearchProviderEnum.Forgejo).ProviderKind);
    }
}

public sealed class AdapterRegistryProjectionHttpTests(SharedMemorySqliteWebApiFixture fixture)
    : IClassFixture<SharedMemorySqliteWebApiFixture>
{
    /// <summary>
    /// **Validates: Requirements 2.15-2.18, 8.1, 8.4-8.6**
    /// </summary>
    [Fact]
    public async Task ProviderInstanceProjection_ExposesRegisteredMetadataWithoutSecretsOrProviderTraffic()
    {
        using var response = await fixture.Client.GetAsync("/api/provider-instances");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var instances = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(5, instances.Length);

        Assert.All(instances, instance =>
        {
            Assert.True(instance.TryGetProperty("providerKind", out _));
            Assert.False(string.IsNullOrWhiteSpace(instance.GetProperty("adapterVersion").GetString()));
            Assert.True(instance.TryGetProperty("declaredCapabilities", out _));
            Assert.True(instance.TryGetProperty("settingsSchema", out var schema));
            Assert.True(schema.GetProperty("version").GetInt32() > 0);
        });

        Assert.DoesNotContain("credential", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint", body, StringComparison.OrdinalIgnoreCase);
    }
}
