using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Services;

public sealed record EnvironmentBootstrapHealth(
    bool IsReady,
    bool IsEnabled,
    long? Generation,
    int ImportedCount,
    IReadOnlyList<Guid> CredentialStableIds)
{
    public static EnvironmentBootstrapHealth Disabled { get; } = new(
        IsReady: true,
        IsEnabled: false,
        Generation: null,
        ImportedCount: 0,
        CredentialStableIds: []);

    public static EnvironmentBootstrapHealth Unavailable { get; } = new(
        IsReady: false,
        IsEnabled: true,
        Generation: null,
        ImportedCount: 0,
        CredentialStableIds: []);
}

/// <summary>
/// Publishes only bootstrap status, generation, counts, and stable references. It never
/// retains credential material, fingerprints, configuration keys, or exception details.
/// </summary>
public sealed class EnvironmentBootstrapReadinessState
{
    private EnvironmentBootstrapHealth current = EnvironmentBootstrapHealth.Disabled;

    public EnvironmentBootstrapHealth Current => Volatile.Read(ref current);

    public void Update(EnvironmentBootstrapHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        Volatile.Write(ref current, health);
    }
}

/// <summary>
/// Master startup boundary for environment-managed GitHub and GitLab credentials.
/// A generation is parsed and fingerprinted completely before any row is changed; import,
/// replacement, last-seen updates, and removal reconciliation then commit atomically.
/// </summary>
public sealed class EnvironmentBootstrapService(
    DBContext dbContext,
    IConfiguration configuration,
    CredentialStorageService storageService,
    CredentialStateService stateService,
    IDatabaseUtcClock databaseUtcClock,
    EnvironmentBootstrapReadinessState readinessState,
    CredentialMutationGate mutationGate)
{
    private const string RemovedReason = "RemovedFromEnvironment";
    private const string ReplacedReason = "Replaced";

    public Task<EnvironmentBootstrapHealth> RunAsync(
        CancellationToken cancellationToken = default) =>
        mutationGate.ExecuteAsync(
            () => RunUnderGateAsync(cancellationToken),
            cancellationToken);

    private async Task<EnvironmentBootstrapHealth> RunUnderGateAsync(
        CancellationToken cancellationToken)
    {
        if (!ReadStrictBoolean(SearchPlatformFeatureFlagNames.EnvironmentBootstrapEnabled))
        {
            readinessState.Update(EnvironmentBootstrapHealth.Disabled);
            return EnvironmentBootstrapHealth.Disabled;
        }

        try
        {
            var configuredEntries = ReadConfiguredEntries();
            var result = await ExecuteGenerationAsync(configuredEntries, cancellationToken);
            readinessState.Update(result);
            return result;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // A failed transaction can leave Added/Modified entities in this scoped
            // DbContext. Detach them so later startup services cannot accidentally persist
            // a rejected generation.
            dbContext.ChangeTracker.Clear();
            // Bootstrap failures are deliberately projected as a bounded status only. In
            // particular, configuration material and provider exception text are not logged.
            readinessState.Update(EnvironmentBootstrapHealth.Unavailable);
            return EnvironmentBootstrapHealth.Unavailable;
        }
    }

    private async Task<EnvironmentBootstrapHealth> ExecuteGenerationAsync(
        IReadOnlyList<ConfiguredBootstrapEntry> configuredEntries,
        CancellationToken cancellationToken)
    {
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            // Execution-strategy retries must rebuild the entire generation from durable
            // state rather than replaying previously tracked mutations.
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var instances = await dbContext.SearchProviderInstances
                .Where(instance =>
                    instance.StableId == ProviderInstanceSchema.DefaultGitHubStableId ||
                    instance.StableId == ProviderInstanceSchema.DefaultGitLabStableId ||
                    instance.StableId == ProviderInstanceSchema.DefaultSourcegraphStableId ||
                    instance.StableId == ProviderInstanceSchema.DefaultHuggingFaceStableId ||
                    instance.StableId == ProviderInstanceSchema.DefaultAzureDevOpsStableId ||
                    instance.ProviderKind == SearchProviderEnum.Gitea ||
                    instance.ProviderKind == SearchProviderEnum.Forgejo)
                .ToListAsync(cancellationToken);
            var instanceByKind = instances
                .GroupBy(instance => instance.ProviderKind)
                .ToDictionary(group => group.Key, group => group.First());
            if (!instanceByKind.ContainsKey(SearchProviderEnum.GitHub) ||
                !instanceByKind.ContainsKey(SearchProviderEnum.GitLab))
            {
                throw new InvalidOperationException("Default bootstrap Provider Instances are unavailable.");
            }

            // Fingerprinting every entry before loading or mutating credential rows validates
            // required key material and detects duplicate physical credentials up front.
            var parsedEntries = new List<ParsedBootstrapEntry>(configuredEntries.Count);
            var sourceIds = new HashSet<string>(StringComparer.Ordinal);
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            foreach (var configured in configuredEntries)
            {
                if (!sourceIds.Add(configured.SourceEntryId))
                {
                    throw new InvalidOperationException("A bootstrap source entry ID is duplicated.");
                }

                if (!instanceByKind.TryGetValue(configured.ProviderKind, out var instance))
                {
                    throw new InvalidOperationException($"No Provider Instance is available for '{configured.ProviderKind}'.");
                }
                var fingerprint = storageService.ComputeFingerprint(
                    configured.Material,
                    configured.ProviderKind,
                    instance.StableId);
                var fingerprintIdentity = $"{instance.Id}:{fingerprint.FingerprintKeyVersion}:" +
                                          Convert.ToHexString(fingerprint.Fingerprint);
                if (!fingerprints.Add(fingerprintIdentity))
                {
                    throw new InvalidOperationException("A bootstrap credential is duplicated.");
                }

                parsedEntries.Add(new ParsedBootstrapEntry(
                    configured.SourceEntryId,
                    configured.Material,
                    instance,
                    fingerprint));
            }

            var now = await databaseUtcClock.GetUtcNowAsync(cancellationToken);
            var priorGeneration = await dbContext.SearchProviderTokens
                .Where(credential => credential.SourceGeneration != null)
                .MaxAsync(credential => (long?)credential.SourceGeneration, cancellationToken) ?? 0;
            var generation = checked(priorGeneration + 1);
            var existingCredentials = await dbContext.SearchProviderTokens
                .Include(credential => credential.ProviderInstance)
                .OrderBy(credential => credential.Id)
                .ToListAsync(cancellationToken);
            var importedStableIds = new List<Guid>(parsedEntries.Count);

            foreach (var entry in parsedEntries)
            {
                var sourceCredential = existingCredentials
                    .Where(credential =>
                        credential.Source == CredentialSource.Environment &&
                        !credential.IsArchived &&
                        string.Equals(credential.SourceEntryId, entry.SourceEntryId, StringComparison.Ordinal))
                    .OrderByDescending(credential => credential.CreatedUtc)
                    .ThenByDescending(credential => credential.Id)
                    .FirstOrDefault();

                if (sourceCredential is not null && FingerprintEquals(sourceCredential, entry.Fingerprint))
                {
                    sourceCredential.SourceGeneration = generation;
                    sourceCredential.LastSeenUtc = now;
                    sourceCredential.UpdatedUtc = now;
                    importedStableIds.Add(sourceCredential.StableId);
                    continue;
                }

                var duplicate = existingCredentials.FirstOrDefault(credential =>
                    credential.ProviderInstanceId == entry.ProviderInstance.Id &&
                    FingerprintEquals(credential, entry.Fingerprint));
                if (duplicate is not null)
                {
                    // Reusing material under another source identity would either overwrite
                    // manual provenance or create an ambiguous environment slot.
                    throw new InvalidOperationException("Bootstrap material conflicts with an existing credential identity.");
                }

                var replacement = storageService.CreateProtectedCredential(
                    entry.Material,
                    entry.ProviderInstance,
                    CredentialSource.Environment,
                    entry.SourceEntryId,
                    generation,
                    nowUtc: now);
                dbContext.SearchProviderTokens.Add(replacement);
                existingCredentials.Add(replacement);
                importedStableIds.Add(replacement.StableId);

                if (sourceCredential is not null)
                {
                    stateService.Disable(sourceCredential, ReplacedReason, now);
                    sourceCredential.IsArchived = true;
                    sourceCredential.ReplacedByStableId = replacement.StableId;
                    sourceCredential.UpdatedUtc = now;
                }
            }

            var presentSourceIds = parsedEntries
                .Select(entry => entry.SourceEntryId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var credential in existingCredentials.Where(credential =>
                         credential.Source == CredentialSource.Environment &&
                         !credential.IsArchived &&
                         credential.SourceEntryId is not null &&
                         !presentSourceIds.Contains(credential.SourceEntryId)))
            {
                if (!credential.IsEnabled &&
                    string.Equals(credential.DisabledReason, RemovedReason, StringComparison.Ordinal))
                {
                    continue;
                }

                stateService.Disable(credential, RemovedReason, now);
                credential.UpdatedUtc = now;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new EnvironmentBootstrapHealth(
                IsReady: true,
                IsEnabled: true,
                Generation: generation,
                ImportedCount: importedStableIds.Count,
                CredentialStableIds: importedStableIds.OrderBy(id => id).ToArray());
        });
    }

    private IReadOnlyList<ConfiguredBootstrapEntry> ReadConfiguredEntries()
    {
        var entries = new List<ConfiguredBootstrapEntry>();
        foreach (var section in configuration
                     .GetSection("SearchCredentials:Bootstrap:Entries")
                     .GetChildren()
                     .OrderBy(section => section.Key, StringComparer.Ordinal))
        {
            var sourceEntryId = NormalizeSourceEntryId(section["SourceEntryId"]);
            var providerKind = ParseProviderKind(section["ProviderKind"]);
            var material = ValidateMaterial(section["Material"], providerKind);
            entries.Add(new ConfiguredBootstrapEntry(sourceEntryId, providerKind, material));
        }

        AddDelimitedEntries(entries, "MASTER_GITHUB_TOKENS", "master-github", SearchProviderEnum.GitHub);
        AddDelimitedEntries(entries, "MASTER_GITLAB_TOKENS", "master-gitlab", SearchProviderEnum.GitLab);
        // Wave 16, Task 16.3: Worker credential variables are cut away from the fleet.
        // WORKER_GITHUB_TOKENS / WORKER_GITLAB_TOKENS are no longer read; leftovers in
        // deployment configuration are ignored instead of imported.
        return entries;
    }

    private void AddDelimitedEntries(
        ICollection<ConfiguredBootstrapEntry> entries,
        string configurationKey,
        string sourcePrefix,
        SearchProviderEnum providerKind)
    {
        var configured = configuration[configurationKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        var materials = configured.Split(
            [',', ';', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < materials.Length; index++)
        {
            entries.Add(new ConfiguredBootstrapEntry(
                $"{sourcePrefix}-{index + 1:0000}",
                providerKind,
                ValidateMaterial(materials[index], providerKind)));
        }
    }

    private bool ReadStrictBoolean(string key) =>
        bool.TryParse(configuration[key], out var enabled) && enabled;

    private static string NormalizeSourceEntryId(string? sourceEntryId)
    {
        if (string.IsNullOrWhiteSpace(sourceEntryId))
        {
            throw new InvalidOperationException("A bootstrap source entry ID is required.");
        }

        var normalized = sourceEntryId.Trim();
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
        {
            throw new InvalidOperationException("A bootstrap source entry ID is invalid.");
        }
        return normalized;
    }

    private static SearchProviderEnum ParseProviderKind(string? configuredKind) =>
        configuredKind?.Trim() switch
        {
            "GitHub" => SearchProviderEnum.GitHub,
            "GitLab" => SearchProviderEnum.GitLab,
            _ => throw new InvalidOperationException("The bootstrap Provider Kind is unsupported.")
        };

    private static string ValidateMaterial(string? material, SearchProviderEnum providerKind)
    {
        if (string.IsNullOrWhiteSpace(material))
        {
            throw new InvalidOperationException("Bootstrap credential material is empty.");
        }

        var canonical = material.Trim();
        if (canonical.Any(char.IsControl) || canonical.Length > 512)
        {
            throw new InvalidOperationException("Bootstrap credential material is malformed.");
        }

        var valid = providerKind switch
        {
            SearchProviderEnum.GitHub =>
                (canonical.StartsWith("ghp_", StringComparison.Ordinal) ||
                 canonical.StartsWith("gho_", StringComparison.Ordinal) ||
                 canonical.StartsWith("ghu_", StringComparison.Ordinal) ||
                 canonical.StartsWith("ghs_", StringComparison.Ordinal) ||
                 canonical.StartsWith("ghr_", StringComparison.Ordinal) ||
                 canonical.StartsWith("github_pat_", StringComparison.Ordinal)) &&
                canonical.Length >= 30,
            SearchProviderEnum.GitLab =>
                canonical.StartsWith("glpat-", StringComparison.Ordinal) && canonical.Length >= 26,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidOperationException("Bootstrap credential material is malformed.");
        }
        return canonical;
    }

    private static bool FingerprintEquals(
        SearchProviderToken credential,
        CredentialFingerprint fingerprint) =>
        credential.FingerprintKeyVersion == fingerprint.FingerprintKeyVersion &&
        credential.Fingerprint.AsSpan().SequenceEqual(fingerprint.Fingerprint);

    private sealed record ConfiguredBootstrapEntry(
        string SourceEntryId,
        SearchProviderEnum ProviderKind,
        string Material);

    private sealed record ParsedBootstrapEntry(
        string SourceEntryId,
        string Material,
        SearchProviderInstance ProviderInstance,
        CredentialFingerprint Fingerprint);
}
