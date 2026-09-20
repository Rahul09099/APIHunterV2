using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Services;

public sealed record CredentialProtectionReadinessResult(
    bool RequiredKeysReady,
    bool UsableCredentialsReady,
    bool ProtectedReadVerificationReady,
    bool ProtectedStorageRequired,
    IReadOnlyList<string> Failures)
{
    public bool IsReady =>
        RequiredKeysReady &&
        UsableCredentialsReady &&
        (!ProtectedStorageRequired || ProtectedReadVerificationReady);

    public static CredentialProtectionReadinessResult NotRequired { get; } = new(
        RequiredKeysReady: true,
        UsableCredentialsReady: true,
        ProtectedReadVerificationReady: true,
        ProtectedStorageRequired: false,
        Failures: []);
}

/// <summary>
/// Validates key-version availability and non-secret durable credential state. Normal
/// readiness never decrypts a credential; the explicit migration verification service
/// performs one post-commit, single-use decrypt at a time and records a durable marker.
/// </summary>
public sealed class CredentialProtectionReadinessService(
    DBContext dbContext,
    CredentialProtectionService protectionService,
    CredentialFingerprintService fingerprintService,
    CredentialStorageMigrationGuard migrationGuard,
    IConfiguration configuration)
{
    public async Task<CredentialProtectionReadinessResult> EvaluateAsync(
        CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        var protectedStorageRequired = IsFlagEnabled(
            SearchPlatformFeatureFlagNames.ProtectedStorageEnabled);
        var credentialPathRequired = protectedStorageRequired &&
            (IsFlagEnabled(SearchPlatformFeatureFlagNames.SchedulerEnabled) ||
             IsFlagEnabled(SearchPlatformFeatureFlagNames.MasterScraperClaimsEnabled) ||
             IsFlagEnabled(SearchPlatformFeatureFlagNames.WorkerClaimsEnabled));

        if (!protectedStorageRequired)
        {
            return CredentialProtectionReadinessResult.NotRequired;
        }

        var keysReady = true;
        var usableReady = true;
        var protectedReadsVerified = true;

        IReadOnlyList<CredentialReadinessRow> enabledCredentials;
        try
        {
            enabledCredentials = await dbContext.SearchProviderTokens
                .AsNoTracking()
                .Where(credential => credential.IsEnabled)
                .OrderBy(credential => credential.StableId)
                .Select(credential => new CredentialReadinessRow(
                    credential.StableId,
                    credential.SearchProvider,
                    credential.ProviderInstance == null
                        ? null
                        : credential.ProviderInstance.StableId,
                    credential.ProviderInstance != null && credential.ProviderInstance.IsEnabled,
                    credential.IsArchived,
                    credential.DisabledAtUtc,
                    credential.EnvelopeFormatVersion,
                    credential.ProtectionKeyVersion,
                    credential.ProtectionNonce.Length,
                    credential.ProtectedCiphertext.Length,
                    credential.AuthenticationTag.Length,
                    credential.FingerprintKeyVersion,
                    credential.Fingerprint.Length,
                    credential.UpdatedUtc))
                .ToListAsync(cancellationToken);
        }
        catch
        {
            return new CredentialProtectionReadinessResult(
                RequiredKeysReady: false,
                UsableCredentialsReady: false,
                ProtectedReadVerificationReady: false,
                ProtectedStorageRequired: protectedStorageRequired,
                Failures: ["Credential protection readiness inspection failed."]);
        }

        if (protectedStorageRequired)
        {
            if (!protectionService.IsConfigured)
            {
                failures.Add("The active credential protection key version is unavailable.");
                keysReady = false;
            }
            if (!fingerprintService.IsConfigured)
            {
                failures.Add("The active credential fingerprint key version is unavailable.");
                keysReady = false;
            }
        }

        if (protectionService.IsConfigured &&
            fingerprintService.IsConfigured &&
            protectionService.UsesSameActiveKeyMaterial(fingerprintService))
        {
            failures.Add("Credential protection and fingerprint key purposes are not separated.");
            keysReady = false;
        }

        if (credentialPathRequired && enabledCredentials.Count == 0)
        {
            failures.Add("No enabled provider credential is available for a required credentialed path.");
            usableReady = false;
        }

        foreach (var credential in enabledCredentials)
        {
            if (credential.DisabledAtUtc is not null ||
                credential.StableId == Guid.Empty ||
                credential.ProviderInstanceStableId is null ||
                credential.ProviderInstanceStableId == Guid.Empty ||
                !credential.ProviderInstanceEnabled ||
                credential.IsArchived ||
                credential.ProviderKind is not SearchProviderEnum.GitHub and not SearchProviderEnum.GitLab)
            {
                failures.Add($"Enabled credential {credential.StableId:D} has invalid durable state.");
                usableReady = false;
                protectedReadsVerified = false;
                continue;
            }

            var hasProtectedEnvelope =
                credential.EnvelopeFormatVersion == CredentialProtectionService.CurrentEnvelopeFormatVersion &&
                credential.ProtectionKeyVersion > 0 &&
                credential.NonceLength == CredentialProtectionService.NonceSizeBytes &&
                credential.CiphertextLength > 0 &&
                credential.AuthenticationTagLength == CredentialProtectionService.AuthenticationTagSizeBytes;
            var hasFingerprint =
                credential.FingerprintKeyVersion > 0 && credential.FingerprintLength == 32;

            if (!hasProtectedEnvelope || !hasFingerprint)
            {
                protectedReadsVerified = false;
                if (!migrationGuard.CanReadLegacyPlaintext)
                {
                    failures.Add($"Enabled credential {credential.StableId:D} has no usable protected read path.");
                    usableReady = false;
                }
                if (protectedStorageRequired)
                {
                    failures.Add($"Enabled credential {credential.StableId:D} has not completed protected migration.");
                }
                continue;
            }

            if (!protectionService.HasKeyVersion(credential.ProtectionKeyVersion))
            {
                failures.Add($"Enabled credential {credential.StableId:D} requires an unavailable protection key version.");
                keysReady = false;
                usableReady = false;
                protectedReadsVerified = false;
            }
            if (!fingerprintService.HasKeyVersion(credential.FingerprintKeyVersion))
            {
                failures.Add($"Enabled credential {credential.StableId:D} requires an unavailable fingerprint key version.");
                keysReady = false;
                usableReady = false;
                protectedReadsVerified = false;
            }
        }

        if (enabledCredentials.Count > 0 && protectedReadsVerified)
        {
            try
            {
                var latestCredentialUpdate = enabledCredentials.Max(credential => credential.UpdatedUtc);
                var marker = await dbContext.ReadinessMarkers
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        candidate => candidate.Name == ProviderInstanceSchema.ProtectedReadsReadinessMarkerName,
                        cancellationToken);
                if (marker is null ||
                    !marker.IsReady ||
                    marker.SchemaVersion != ProviderInstanceSchema.CurrentVersion ||
                    marker.UpdatedUtc < latestCredentialUpdate)
                {
                    failures.Add("Enabled credentials have not completed explicit protected-read verification.");
                    protectedReadsVerified = false;
                }
            }
            catch
            {
                failures.Add("Protected-read verification marker inspection failed.");
                protectedReadsVerified = false;
            }
        }

        return new CredentialProtectionReadinessResult(
            RequiredKeysReady: keysReady,
            UsableCredentialsReady: usableReady,
            ProtectedReadVerificationReady: protectedReadsVerified,
            ProtectedStorageRequired: protectedStorageRequired,
            Failures: failures.Distinct(StringComparer.Ordinal).ToArray());
    }

    public Task<CredentialProtectionReadinessResult> EvaluateReadinessAsync(
        CancellationToken cancellationToken = default) =>
        EvaluateAsync(cancellationToken);

    private bool IsFlagEnabled(string key) =>
        bool.TryParse(configuration[key], out var enabled) && enabled;

    private sealed record CredentialReadinessRow(
        Guid StableId,
        SearchProviderEnum ProviderKind,
        Guid? ProviderInstanceStableId,
        bool ProviderInstanceEnabled,
        bool IsArchived,
        DateTime? DisabledAtUtc,
        int EnvelopeFormatVersion,
        int ProtectionKeyVersion,
        int NonceLength,
        int CiphertextLength,
        int AuthenticationTagLength,
        int FingerprintKeyVersion,
        int FingerprintLength,
        DateTime UpdatedUtc);
}
