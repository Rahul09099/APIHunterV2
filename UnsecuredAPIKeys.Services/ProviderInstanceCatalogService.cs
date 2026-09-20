using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

public sealed record ProviderInstanceAdapterProjection(
    Guid StableId,
    SearchProviderEnum ProviderKind,
    string DisplayName,
    string NormalizedScheme,
    string NormalizedHost,
    int NormalizedPort,
    string NormalizedBasePath,
    bool IsEnabled,
    bool AllowGlobalPublicSearch,
    int MaxConcurrentOperations,
    int SettingsVersion,
    bool EndpointPolicyApproved,
    int EndpointPolicyVersion,
    DateTime? EndpointApprovedAtUtc,
    string AdapterVersion,
    SearchProviderCapability DeclaredCapabilities,
    ProviderSettingsSchema SettingsSchema);

public sealed class ProviderInstanceCatalogService(
    DBContext dbContext,
    SearchProviderAdapterRegistry adapterRegistry)
{
    public async Task<IReadOnlyList<ProviderInstanceAdapterProjection>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var instances = await dbContext.SearchProviderInstances
            .AsNoTracking()
            .OrderBy(instance => instance.ProviderKind)
            .ThenBy(instance => instance.StableId)
            .Select(instance => new
            {
                instance.StableId,
                instance.ProviderKind,
                instance.DisplayName,
                instance.NormalizedScheme,
                instance.NormalizedHost,
                instance.NormalizedPort,
                instance.NormalizedBasePath,
                instance.IsEnabled,
                instance.AllowGlobalPublicSearch,
                instance.MaxConcurrentOperations,
                instance.SettingsVersion,
                instance.ApprovedByTelegramId,
                instance.EndpointPolicyVersion,
                instance.ApprovedEndpointIdentity,
                instance.EndpointApprovedAtUtc,
                instance.DevelopmentHttpAllowed,
                instance.PrivateNetworkAllowlistJson
            })
            .ToListAsync(cancellationToken);

        return instances.Select(instance =>
        {
            var adapter = adapterRegistry.GetRequiredAdapter(instance.ProviderKind);
            AdapterKindGuard.RequireMatchingKind(adapter.ProviderKind, instance.ProviderKind);

            return new ProviderInstanceAdapterProjection(
                instance.StableId,
                instance.ProviderKind,
                instance.DisplayName,
                instance.NormalizedScheme,
                instance.NormalizedHost,
                instance.NormalizedPort,
                instance.NormalizedBasePath,
                instance.IsEnabled,
                instance.AllowGlobalPublicSearch,
                instance.MaxConcurrentOperations,
                instance.SettingsVersion,
                IsManagedSaas(instance.StableId) || EndpointPolicy.ValidateApprovedIdentity(new UnsecuredAPIKeys.Data.Models.SearchProviderInstance
                {
                    StableId = instance.StableId,
                    NormalizedScheme = instance.NormalizedScheme,
                    NormalizedHost = instance.NormalizedHost,
                    NormalizedPort = instance.NormalizedPort,
                    NormalizedBasePath = instance.NormalizedBasePath,
                    ApprovedByTelegramId = instance.ApprovedByTelegramId,
                    EndpointPolicyVersion = instance.EndpointPolicyVersion,
                    ApprovedEndpointIdentity = instance.ApprovedEndpointIdentity,
                    EndpointApprovedAtUtc = instance.EndpointApprovedAtUtc,
                    DevelopmentHttpAllowed = instance.DevelopmentHttpAllowed,
                    PrivateNetworkAllowlistJson = instance.PrivateNetworkAllowlistJson
                }).Allowed,
                instance.EndpointPolicyVersion,
                instance.EndpointApprovedAtUtc,
                adapter.AdapterVersion,
                adapter.DeclaredCapabilities,
                adapter.SettingsSchema);
        }).ToArray();
    }

    private static bool IsManagedSaas(Guid stableId) =>
        stableId == ProviderInstanceSchema.DefaultGitHubStableId ||
        stableId == ProviderInstanceSchema.DefaultGitLabStableId ||
        stableId == ProviderInstanceSchema.DefaultSourcegraphStableId ||
        stableId == ProviderInstanceSchema.DefaultHuggingFaceStableId ||
        stableId == ProviderInstanceSchema.DefaultAzureDevOpsStableId;
}
