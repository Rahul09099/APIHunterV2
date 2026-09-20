using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Managed-SaaS content location for one normalized search result (Wave 13).
/// Maps provider results to adapter content inputs without branching at call sites:
/// GitLab provenance URLs are raw-file API URLs; GitHub results resolve through the
/// repository contents API from owner/name/path/ref. Returns null when the result
/// lacks the fields content retrieval requires. Self-hosted origins are out of scope.
/// </summary>
public static class WorkerContentLocator
{
    public sealed record ContentLocation(
        string? ContentApiUrl,
        string? ContentPath,
        string? ContentRevision,
        string? ContentRepositoryOwner,
        string? ContentRepositoryName);

    public static ContentLocation? Resolve(
        ProviderResultInput input,
        ValidatedProviderInstance instance)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(instance);

        if (input.ProviderKind == SearchProviderEnum.GitLab)
        {
            if (string.IsNullOrWhiteSpace(input.ProvenanceUrl))
            {
                return null;
            }

            return new ContentLocation(
                input.ProvenanceUrl, null, null, null, null);
        }

        if (input.ProviderKind == SearchProviderEnum.GitHub)
        {
            if (string.IsNullOrWhiteSpace(input.NormalizedFilePath) ||
                string.IsNullOrWhiteSpace(input.RepositoryOwner) ||
                string.IsNullOrWhiteSpace(input.RepositoryName))
            {
                return null;
            }

            return new ContentLocation(
                null,
                input.NormalizedFilePath,
                string.IsNullOrWhiteSpace(input.ImmutableRevisionOrEquivalentVersion)
                    ? null
                    : input.ImmutableRevisionOrEquivalentVersion,
                input.RepositoryOwner,
                input.RepositoryName);
        }

        return null;
    }
}
