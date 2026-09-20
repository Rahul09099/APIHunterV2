using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;

namespace UnsecuredAPIKeys.Providers;

public sealed class SearchProviderAdapterRegistry
{
    private readonly IReadOnlyDictionary<SearchProviderEnum, ISearchProviderAdapter> adapters;

    public SearchProviderAdapterRegistry(IEnumerable<ISearchProviderAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var registrations = adapters.ToArray();
        var duplicateKinds = registrations
            .GroupBy(adapter => adapter.ProviderKind)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicateKinds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Multiple search-provider adapters are registered for: {string.Join(", ", duplicateKinds)}.");
        }

        if (registrations.Any(adapter => adapter.ProviderKind == SearchProviderEnum.Unknown))
        {
            throw new InvalidOperationException("An adapter cannot be registered for the Unknown Provider Kind.");
        }

        this.adapters = registrations.ToDictionary(adapter => adapter.ProviderKind);
    }

    public ISearchProviderAdapter GetRequiredAdapter(SearchProviderEnum providerKind)
    {
        if (providerKind == SearchProviderEnum.Unknown || !adapters.TryGetValue(providerKind, out var adapter))
        {
            throw new KeyNotFoundException($"No search-provider adapter is registered for Provider Kind '{providerKind}'.");
        }

        return adapter;
    }
}

public static class SearchProviderCapabilityService
{
    public static SearchProviderCapability CalculateEffectiveCapabilities(
        SearchProviderCapability declaredCapabilities,
        SearchProviderCapability discoveredCapabilities,
        bool discoverySucceeded) =>
        discoverySucceeded
            ? declaredCapabilities & discoveredCapabilities
            : SearchProviderCapability.None;
}

public static class AdapterKindGuard
{
    public static void RequireMatchingKind(
        SearchProviderEnum adapterKind,
        SearchProviderEnum providerInstanceKind)
    {
        if (adapterKind == SearchProviderEnum.Unknown || adapterKind != providerInstanceKind)
        {
            throw new InvalidOperationException(
                $"Adapter Provider Kind '{adapterKind}' does not match Provider Instance kind '{providerInstanceKind}'.");
        }
    }
}

public static class SearchProviderAdapterRegistration
{
    public static IServiceCollection AddSearchProviderAdapters(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<GitHubSearchProvider>();
        services.TryAddSingleton<GitLabSearchProvider>(serviceProvider =>
            new GitLabSearchProvider(
                serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("GitLabLegacySearch"),
                serviceProvider.GetService<Microsoft.Extensions.Logging.ILogger<GitLabSearchProvider>>()));

        services.AddSingleton<ISearchProviderAdapter>(serviceProvider =>
            new GitHubSearchProviderAdapter(
                serviceProvider.GetService<IHttpClientFactory>(),
                serviceProvider.GetService<ISearchProviderOutcomeClassifier>(),
                serviceProvider.GetService<GitHubSearchProvider>()));
        services.AddSingleton<ISearchProviderAdapter>(serviceProvider =>
            new GitLabSearchProviderAdapter(
                serviceProvider.GetService<IHttpClientFactory>(),
                serviceProvider.GetService<ISearchProviderOutcomeClassifier>(),
                serviceProvider.GetService<GitLabSearchProvider>()));
        services.AddSingleton<ISearchProviderAdapter>(serviceProvider =>
            new SourcegraphSearchProviderAdapter(
                serviceProvider.GetService<IHttpClientFactory>(),
                serviceProvider.GetService<ISearchProviderOutcomeClassifier>()));
        services.AddSingleton<ISearchProviderAdapter>(serviceProvider =>
            new HuggingFaceSearchProviderAdapter(
                serviceProvider.GetService<IHttpClientFactory>(),
                serviceProvider.GetService<ISearchProviderOutcomeClassifier>()));
        services.AddSingleton<ISearchProviderAdapter>(serviceProvider =>
            new AzureDevOpsSearchProviderAdapter(
                serviceProvider.GetService<IHttpClientFactory>(),
                serviceProvider.GetService<ISearchProviderOutcomeClassifier>()));
        services.AddSingleton<ISearchProviderAdapter>(serviceProvider =>
            new GiteaSearchProviderAdapter(
                serviceProvider.GetService<IHttpClientFactory>(),
                serviceProvider.GetService<ISearchProviderOutcomeClassifier>()));
        services.AddSingleton<ISearchProviderAdapter>(serviceProvider =>
            new ForgejoSearchProviderAdapter(
                serviceProvider.GetService<IHttpClientFactory>(),
                serviceProvider.GetService<ISearchProviderOutcomeClassifier>()));
        services.TryAddSingleton<SearchProviderAdapterRegistry>();
        return services;
    }

    public static SearchProviderAdapterRegistry CreateDefaultRegistry(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        return new SearchProviderAdapterRegistry(
        [
            new GitHubSearchProviderAdapter(new GitHubSearchProvider()),
            new GitLabSearchProviderAdapter(
                new GitLabSearchProvider(httpClientFactory.CreateClient("GitLabLegacySearch"))),
            new SourcegraphSearchProviderAdapter(httpClientFactory.CreateClient("SourcegraphSearch")),
            new HuggingFaceSearchProviderAdapter(httpClientFactory.CreateClient("HuggingFaceSearch")),
            new AzureDevOpsSearchProviderAdapter(httpClientFactory.CreateClient("AzureDevOpsSearch")),
            new GiteaSearchProviderAdapter(httpClientFactory.CreateClient("GiteaSearch")),
            new ForgejoSearchProviderAdapter(httpClientFactory.CreateClient("ForgejoSearch"))
        ]);
    }
}
