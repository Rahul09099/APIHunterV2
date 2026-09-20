using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Single shared endpoint-validation rule for operation execution (Wave 14).
/// Managed-SaaS defaults carry no per-instance approval (readiness treats them as safe);
/// every other instance requires current approval and per-operation DNS validation.
/// Used identically by credentialed (Master) and credential-free (public) paths so the
/// endpoint gate cannot diverge between them.
/// </summary>
internal static class ProviderInstanceOperationValidation
{
    internal static bool IsManagedSaasInstance(Guid stableId) =>
        stableId == ProviderInstanceSchema.DefaultGitHubStableId ||
        stableId == ProviderInstanceSchema.DefaultGitLabStableId ||
        stableId == ProviderInstanceSchema.DefaultSourcegraphStableId ||
        stableId == ProviderInstanceSchema.DefaultHuggingFaceStableId ||
        stableId == ProviderInstanceSchema.DefaultAzureDevOpsStableId;

    internal static async Task<(ValidatedProviderInstance Instance, ProviderOperationBounds Bounds)>
        ValidateForOperationAsync(
            SearchProviderInstance instance,
            EndpointPolicy endpointPolicy,
            EndpointPolicyOptions options,
            EndpointOperationKind operation,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(endpointPolicy);
        ArgumentNullException.ThrowIfNull(options);

        if (IsManagedSaasInstance(instance.StableId))
        {
            return (
                new ValidatedProviderInstance(
                    instance.StableId,
                    instance.ProviderKind,
                    instance.NormalizedScheme,
                    instance.NormalizedHost,
                    instance.NormalizedPort,
                    instance.NormalizedBasePath,
                    instance.SettingsVersion,
                    instance.SettingsJson),
                new ProviderOperationBounds(
                    options.MaxResponseSizeBytes,
                    options.AllowedContentTypes,
                    options.MaxEventCount,
                    options.MaxContentConcurrency,
                    options.RequestTimeout));
        }

        var validated = await endpointPolicy.ValidateForOperationAsync(
            instance, operation, cancellationToken);
        return (validated.ProviderInstance, validated.Bounds);
    }
}
