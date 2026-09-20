using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Explicit compatibility gate for the pre-scrub migration window. Missing or malformed
/// values deny plaintext reads and writes.
/// </summary>
public sealed class CredentialStorageMigrationGuard
{
    public const string PreScrubGuardEnabled =
        "SearchCredentials:Migration:PreScrubGuardEnabled";
    public const string LegacyPlaintextReadEnabled =
        "SearchCredentials:Migration:LegacyPlaintextReadEnabled";
    public const string LegacyPlaintextWriteEnabled =
        "SearchCredentials:Migration:LegacyPlaintextWriteEnabled";
    public const string VerifyProtectedReadsOnStartup =
        "SearchCredentials:Migration:VerifyProtectedReadsOnStartup";

    public CredentialStorageMigrationGuard(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IsPreScrubGuardEnabled = ReadStrictBoolean(configuration, PreScrubGuardEnabled);
        var readRequested = ReadStrictBoolean(configuration, LegacyPlaintextReadEnabled);
        var writeRequested = ReadStrictBoolean(configuration, LegacyPlaintextWriteEnabled);
        var verificationRequested = ReadStrictBoolean(configuration, VerifyProtectedReadsOnStartup);
        CanReadLegacyPlaintext = IsPreScrubGuardEnabled && readRequested;
        CanRetainLegacyPlaintextOnProtectedWrite =
            CanReadLegacyPlaintext && writeRequested && !verificationRequested;
        CanVerifyProtectedReadsOnStartup =
            IsPreScrubGuardEnabled && verificationRequested;

        if (IsPreScrubGuardEnabled && writeRequested && !readRequested)
        {
            throw new CredentialProtectionException(
                "Legacy plaintext retention requires the guarded dual-read migration window.");
        }
        if (writeRequested && verificationRequested)
        {
            throw new CredentialProtectionException(
                "Legacy plaintext writes must be disabled before protected-read verification.");
        }
    }

    public bool IsPreScrubGuardEnabled { get; }
    public bool CanReadLegacyPlaintext { get; }
    public bool CanRetainLegacyPlaintextOnProtectedWrite { get; }
    public bool CanVerifyProtectedReadsOnStartup { get; }

    public void DemandLegacyRead()
    {
        if (!CanReadLegacyPlaintext)
        {
            throw new CredentialProtectionException(
                "Legacy plaintext credential access is outside the pre-scrub migration guard.");
        }
    }

    private static bool ReadStrictBoolean(IConfiguration configuration, string key) =>
        bool.TryParse(configuration[key], out var enabled) && enabled;
}

/// <summary>
/// Creates protected credential rows. Every write produces an authenticated envelope and
/// keyed fingerprint; retaining a compatibility plaintext copy requires the explicit guard.
/// </summary>
public sealed class CredentialStorageService(
    CredentialProtectionService protectionService,
    CredentialFingerprintService fingerprintService,
    CredentialStorageMigrationGuard migrationGuard)
{
    public SearchProviderToken CreateProtectedCredential(
        string material,
        SearchProviderInstance providerInstance,
        CredentialSource source = CredentialSource.Manual,
        string? sourceEntryId = null,
        long? sourceGeneration = null,
        long? addedByTelegramId = null,
        DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(providerInstance);
        var requestedNow = nowUtc ?? DateTime.UtcNow;
        if (requestedNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Credential write time must be UTC.", nameof(nowUtc));
        }

        // SQLite's authoritative clock and DateTime mapping preserve milliseconds. Keep
        // application-originated creation times at the same precision so a command in the
        // same millisecond cannot observe database UTC preceding the credential's creation.
        var now = new DateTime(
            requestedNow.Ticks - requestedNow.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);

        var credential = new SearchProviderToken
        {
            StableId = Guid.NewGuid(),
            SearchProvider = providerInstance.ProviderKind,
            ProviderInstanceId = providerInstance.Id,
            ProviderInstance = providerInstance,
            Source = source,
            SourceEntryId = NormalizeSourceEntryId(sourceEntryId),
            SourceGeneration = sourceGeneration,
            LastSeenUtc = source == CredentialSource.Environment ? now : null,
            IsEnabled = true,
            DisabledReason = null,
            DisabledAtUtc = null,
            Revision = 0,
            CreatedUtc = now,
            UpdatedUtc = now,
            AddedByTelegramId = addedByTelegramId
        };
        ProtectForWrite(credential, material, providerInstance);
        return credential;
    }

    public void ProtectForWrite(
        SearchProviderToken credential,
        string material,
        SearchProviderInstance providerInstance)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(providerInstance);
        EnsureKeyPurposeSeparation();
        var canonicalMaterial = CanonicalizeMaterial(material);
        if (credential.StableId == Guid.Empty || providerInstance.StableId == Guid.Empty ||
            credential.SearchProvider != providerInstance.ProviderKind)
        {
            throw new CredentialProtectionException("Credential write identity is invalid.");
        }

        var identity = new CredentialProtectionContext(
            credential.StableId,
            providerInstance.ProviderKind,
            providerInstance.StableId);
        var envelope = protectionService.Protect(canonicalMaterial, identity);
        var fingerprint = fingerprintService.ComputeFingerprint(canonicalMaterial, identity);

        credential.ProviderInstanceId = providerInstance.Id;
        credential.ProviderInstance = providerInstance;
        credential.EnvelopeFormatVersion = envelope.EnvelopeFormatVersion;
        credential.ProtectionKeyVersion = envelope.ProtectionKeyVersion;
        credential.ProtectionNonce = envelope.Nonce;
        credential.ProtectedCiphertext = envelope.Ciphertext;
        credential.AuthenticationTag = envelope.AuthenticationTag;
        credential.FingerprintKeyVersion = fingerprint.FingerprintKeyVersion;
        credential.Fingerprint = fingerprint.Fingerprint;
        credential.Token = migrationGuard.CanRetainLegacyPlaintextOnProtectedWrite
            ? canonicalMaterial
            : string.Empty;
    }

    public CredentialFingerprint ComputeFingerprint(
        string material,
        SearchProviderEnum providerKind,
        Guid providerInstanceStableId)
    {
        EnsureKeyPurposeSeparation();
        var canonicalMaterial = CanonicalizeMaterial(material);
        // Fingerprints intentionally exclude credential Stable ID so duplicate material on
        // the same instance resolves to one durable credential identity.
        var fingerprintContext = new CredentialProtectionContext(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            providerKind,
            providerInstanceStableId);
        return fingerprintService.ComputeFingerprint(canonicalMaterial, fingerprintContext);
    }

    public static bool IsProtected(SearchProviderToken credential) =>
        credential.EnvelopeFormatVersion > 0 &&
        credential.ProtectionKeyVersion > 0 &&
        credential.ProtectionNonce.Length == CredentialProtectionService.NonceSizeBytes &&
        credential.ProtectedCiphertext.Length > 0 &&
        credential.AuthenticationTag.Length == CredentialProtectionService.AuthenticationTagSizeBytes &&
        credential.FingerprintKeyVersion > 0 &&
        credential.Fingerprint.Length == 32;

    private void EnsureKeyPurposeSeparation()
    {
        if (protectionService.UsesSameActiveKeyMaterial(fingerprintService))
        {
            throw new CredentialProtectionException(
                "Credential protection and fingerprint keys must use distinct key material.");
        }
    }

    private static string CanonicalizeMaterial(string material)
    {
        if (string.IsNullOrWhiteSpace(material))
        {
            throw new CredentialProtectionException("Credential material is required.");
        }

        var canonical = material.Trim();
        if (canonical.Any(char.IsControl))
        {
            throw new CredentialProtectionException("Credential material contains unsupported characters.");
        }
        return canonical;
    }

    private static string? NormalizeSourceEntryId(string? sourceEntryId)
    {
        if (sourceEntryId is null)
        {
            return null;
        }

        var normalized = sourceEntryId.Trim();
        if (normalized.Length is 0 or > 256 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Credential source entry ID must be bounded non-secret text.",
                nameof(sourceEntryId));
        }
        return normalized;
    }
}

public sealed record CredentialOperationReference(
    Guid CredentialStableId,
    SearchProviderEnum ProviderKind,
    Guid ProviderInstanceStableId,
    long ExpectedRevision,
    Guid? LeaseId = null);

/// <summary>
/// Single-use capability minted after the selecting transaction commits.
/// </summary>
public sealed class CredentialOperationGrant
{
    private int _consumed;

    internal CredentialOperationGrant(
        CredentialOperationReference credential,
        Guid operationId)
    {
        Credential = credential;
        OperationId = operationId;
    }

    internal CredentialOperationReference Credential { get; }
    public Guid OperationId { get; }

    internal void Consume()
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
        {
            throw new CredentialProtectionException(
                "The current-operation credential grant has already been consumed.");
        }
    }
}

/// <summary>
/// Ephemeral current-operation material. Disposal releases the managed reference; callers
/// must never cache, serialize, log, or place this value in a query string.
/// </summary>
public sealed class CurrentOperationCredentialMaterial : IDisposable
{
    internal CurrentOperationCredentialMaterial(Guid stableId, Guid operationId, string value)
    {
        CredentialStableId = stableId;
        OperationId = operationId;
        Value = value;
    }

    public Guid CredentialStableId { get; }
    public Guid OperationId { get; }
    public string Value { get; private set; }

    public void Dispose() => Value = string.Empty;
}

/// <summary>
/// Bounded decrypt boundary. It loads and decrypts one explicitly granted credential only,
/// after verifying there is no active selecting transaction.
/// </summary>
public sealed class CredentialMaterialAccessService(
    DBContext dbContext,
    CredentialProtectionService protectionService,
    CredentialStorageMigrationGuard migrationGuard)
{
    public CredentialOperationGrant GrantAfterCommit(
        CredentialOperationReference credential,
        Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "A current-operation credential grant can be minted only after commit.");
        }
        if (credential.CredentialStableId == Guid.Empty ||
            credential.ProviderInstanceStableId == Guid.Empty ||
            credential.ExpectedRevision < 0 ||
            operationId == Guid.Empty)
        {
            throw new ArgumentException("Current-operation credential identity is invalid.");
        }

        return new CredentialOperationGrant(credential, operationId);
    }

    /// <summary>
    /// Loads only the non-secret durable identity for one selected credential and mints
    /// its single-use capability after the caller's selecting transaction has committed.
    /// </summary>
    public async Task<CredentialOperationGrant> GrantStoredCredentialAfterCommitAsync(
        Guid credentialStableId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "A current-operation credential grant can be minted only after commit.");
        }
        if (credentialStableId == Guid.Empty || operationId == Guid.Empty)
        {
            throw new ArgumentException("Current-operation credential identity is invalid.");
        }

        var reference = await dbContext.SearchProviderTokens
            .AsNoTracking()
            .Where(credential => credential.StableId == credentialStableId)
            .Select(credential => new CredentialOperationReference(
                credential.StableId,
                credential.SearchProvider,
                credential.ProviderInstance!.StableId,
                credential.Revision,
                credential.LeaseId))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new CredentialProtectionException("The selected credential is unavailable.");

        return GrantAfterCommit(reference, operationId);
    }

    public async Task<CurrentOperationCredentialMaterial> DecryptGrantedAsync(
        CredentialOperationGrant grant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "Credential decryption is prohibited inside the selecting transaction.");
        }
        grant.Consume();

        var expected = grant.Credential;
        var stored = await dbContext.SearchProviderTokens
            .AsNoTracking()
            .Where(credential => credential.StableId == expected.CredentialStableId)
            .Select(credential => new StoredCredentialEnvelope(
                credential.StableId,
                credential.SearchProvider,
                credential.ProviderInstance!.StableId,
                credential.IsEnabled,
                credential.DisabledAtUtc,
                credential.Revision,
                credential.LeaseId,
                credential.EnvelopeFormatVersion,
                credential.ProtectionKeyVersion,
                credential.ProtectionNonce,
                credential.ProtectedCiphertext,
                credential.AuthenticationTag))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new CredentialProtectionException("The granted credential is unavailable.");

        if (!stored.IsEnabled || stored.DisabledAtUtc is not null ||
            stored.ProviderKind != expected.ProviderKind ||
            stored.ProviderInstanceStableId != expected.ProviderInstanceStableId ||
            stored.Revision != expected.ExpectedRevision ||
            (expected.LeaseId is not null && stored.LeaseId != expected.LeaseId))
        {
            throw new CredentialProtectionException(
                "The current-operation credential grant no longer matches durable state.");
        }

        string material;
        if (stored.EnvelopeFormatVersion > 0)
        {
            material = protectionService.Unprotect(
                new CredentialProtectionEnvelope
                {
                    EnvelopeFormatVersion = stored.EnvelopeFormatVersion,
                    ProtectionKeyVersion = stored.ProtectionKeyVersion,
                    Nonce = stored.ProtectionNonce,
                    Ciphertext = stored.ProtectedCiphertext,
                    AuthenticationTag = stored.AuthenticationTag
                },
                new CredentialProtectionContext(
                    stored.StableId,
                    stored.ProviderKind,
                    stored.ProviderInstanceStableId));
        }
        else
        {
            migrationGuard.DemandLegacyRead();
            material = await dbContext.SearchProviderTokens
                .AsNoTracking()
                .Where(credential => credential.StableId == expected.CredentialStableId)
                .Select(credential => credential.Token)
                .SingleAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(material))
            {
                throw new CredentialProtectionException(
                    "The guarded legacy credential is unavailable.");
            }
        }

        return new CurrentOperationCredentialMaterial(
            stored.StableId,
            grant.OperationId,
            material);
    }

    private sealed record StoredCredentialEnvelope(
        Guid StableId,
        SearchProviderEnum ProviderKind,
        Guid ProviderInstanceStableId,
        bool IsEnabled,
        DateTime? DisabledAtUtc,
        long Revision,
        Guid? LeaseId,
        int EnvelopeFormatVersion,
        int ProtectionKeyVersion,
        byte[] ProtectionNonce,
        byte[] ProtectedCiphertext,
        byte[] AuthenticationTag);
}

public sealed record CredentialMigrationVerificationResult(
    bool AllEnabledCredentialsUseProtectedReads,
    IReadOnlyList<Guid> FailedCredentialStableIds);

/// <summary>
/// Explicit pre-scrub migration operations. These methods are never called by normal
/// runtime reads and report only stable references.
/// </summary>
public sealed class CredentialPlaintextMigrationService(
    DBContext dbContext,
    CredentialStorageService storageService,
    CredentialStorageMigrationGuard migrationGuard,
    CredentialMaterialAccessService materialAccessService,
    IDatabaseUtcClock databaseUtcClock,
    CredentialMutationGate? mutationGate = null)
{
    private readonly CredentialMutationGate effectiveMutationGate = mutationGate ?? new();

    public Task<int> ProtectLegacyCredentialsAsync(
        CancellationToken cancellationToken = default) =>
        effectiveMutationGate.ExecuteAsync(
            () => ProtectLegacyCredentialsUnderGateAsync(cancellationToken),
            cancellationToken);

    private async Task<int> ProtectLegacyCredentialsUnderGateAsync(
        CancellationToken cancellationToken)
    {
        migrationGuard.DemandLegacyRead();
        // A legacy database may contain duplicate plaintext rows that acquire the same
        // fingerprint during this backfill. Uniqueness is restored by reconciliation only
        // after every row has been protected.
        await CredentialFingerprintIdentityIndex.DropAsync(dbContext, cancellationToken);
        await SetProtectedReadVerificationMarkerAsync(false, cancellationToken);
        var ids = await dbContext.SearchProviderTokens
            .AsNoTracking()
            .Where(credential => credential.EnvelopeFormatVersion == 0)
            .OrderBy(credential => credential.Id)
            .Select(credential => credential.Id)
            .ToListAsync(cancellationToken);

        var protectedCount = 0;
        foreach (var id in ids)
        {
            var credential = await dbContext.SearchProviderTokens
                .Include(candidate => candidate.ProviderInstance)
                .SingleAsync(candidate => candidate.Id == id, cancellationToken);
            if (credential.ProviderInstance is null || string.IsNullOrWhiteSpace(credential.Token))
            {
                throw new CredentialProtectionException(
                    "A legacy credential cannot be protected because required migration material is unavailable.");
            }

            storageService.ProtectForWrite(
                credential,
                credential.Token,
                credential.ProviderInstance);
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.Entry(credential).State = EntityState.Detached;
            protectedCount++;
        }

        return protectedCount;
    }

    public async Task<CredentialMigrationVerificationResult> VerifyEnabledProtectedReadsAsync(
        CancellationToken cancellationToken = default)
    {
        await SetProtectedReadVerificationMarkerAsync(false, cancellationToken);
        var references = await dbContext.SearchProviderTokens
            .AsNoTracking()
            .Where(credential => credential.IsEnabled)
            .OrderBy(credential => credential.StableId)
            .Select(credential => new
            {
                credential.StableId,
                credential.SearchProvider,
                ProviderInstanceStableId = credential.ProviderInstance!.StableId,
                credential.Revision,
                credential.LeaseId,
                credential.EnvelopeFormatVersion
            })
            .ToListAsync(cancellationToken);
        var failed = new List<Guid>();

        foreach (var reference in references)
        {
            if (reference.EnvelopeFormatVersion <= 0)
            {
                failed.Add(reference.StableId);
                continue;
            }

            try
            {
                var grant = materialAccessService.GrantAfterCommit(
                    new CredentialOperationReference(
                        reference.StableId,
                        reference.SearchProvider,
                        reference.ProviderInstanceStableId,
                        reference.Revision,
                        reference.LeaseId),
                    Guid.NewGuid());
                using var material = await materialAccessService.DecryptGrantedAsync(
                    grant,
                    cancellationToken);
            }
            catch
            {
                failed.Add(reference.StableId);
            }
        }

        var verified = failed.Count == 0;
        await SetProtectedReadVerificationMarkerAsync(verified, cancellationToken);
        return new CredentialMigrationVerificationResult(verified, failed);
    }

    /// <summary>
    /// Scrub result for the post-cutover plaintext purge (Wave 16, Task 16.4).
    /// </summary>
    public sealed record PlaintextScrubResult(int RowsScrubbed);

    /// <summary>
    /// Result of the no-plaintext gate check.
    /// </summary>
    public sealed record NoPlaintextReport(
        bool Clean,
        int RowsWithPlaintext,
        IReadOnlyList<Guid> CredentialStableIds);

    /// <summary>
    /// Clears the legacy <c>Token</c> column for every protected credential (Wave 16,
    /// Task 16.4). Fail-closed: any row that still carries plaintext without a protection
    /// envelope aborts the scrub so it must be protected first. Idempotent and
    /// reversible by re-import; the column itself is retained for pre-scrub compatibility.
    /// </summary>
    public Task<PlaintextScrubResult> ScrubLegacyPlaintextAsync(
        CancellationToken cancellationToken = default) =>
        effectiveMutationGate.ExecuteAsync(
            () => ScrubLegacyPlaintextUnderGateAsync(cancellationToken),
            cancellationToken);

    private async Task<PlaintextScrubResult> ScrubLegacyPlaintextUnderGateAsync(
        CancellationToken cancellationToken)
    {
        var unprotected = await dbContext.SearchProviderTokens
            .AsNoTracking()
            .Where(credential =>
                !string.IsNullOrEmpty(credential.Token) &&
                credential.EnvelopeFormatVersion <= 0)
            .Select(credential => credential.StableId)
            .ToListAsync(cancellationToken);
        if (unprotected.Count > 0)
        {
            throw new CredentialProtectionException(
                "Plaintext scrub is blocked until every plaintext credential is protected.");
        }

        var ids = await dbContext.SearchProviderTokens
            .Where(credential =>
                !string.IsNullOrEmpty(credential.Token) &&
                credential.EnvelopeFormatVersion > 0)
            .Select(credential => credential.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in ids)
        {
            var credential = await dbContext.SearchProviderTokens
                .SingleAsync(candidate => candidate.Id == id, cancellationToken);
            credential.Token = string.Empty;
            credential.UpdatedUtc = await databaseUtcClock.GetUtcNowAsync(cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new PlaintextScrubResult(ids.Count);
    }

    /// <summary>
    /// Gate check proving no row retains legacy plaintext (Wave 16, Tasks 16.4–16.5).
    /// Reports stable references only, never material.
    /// </summary>
    public async Task<NoPlaintextReport> VerifyNoPlaintextRetainedAsync(
        CancellationToken cancellationToken = default)
    {
        var stableIds = await dbContext.SearchProviderTokens
            .AsNoTracking()
            .Where(credential => !string.IsNullOrEmpty(credential.Token))
            .OrderBy(credential => credential.StableId)
            .Select(credential => credential.StableId)
            .ToListAsync(cancellationToken);
        return new NoPlaintextReport(stableIds.Count == 0, stableIds.Count, stableIds);
    }

    private async Task SetProtectedReadVerificationMarkerAsync(
        bool isReady,
        CancellationToken cancellationToken)
    {
        var now = await databaseUtcClock.GetUtcNowAsync(cancellationToken);
        var marker = await dbContext.ReadinessMarkers.SingleOrDefaultAsync(
            candidate => candidate.Name == ProviderInstanceSchema.ProtectedReadsReadinessMarkerName,
            cancellationToken);
        if (marker is null)
        {
            marker = new ReadinessMarker
            {
                Id = ProviderInstanceSchema.ProtectedReadsMarkerRecordId,
                Name = ProviderInstanceSchema.ProtectedReadsReadinessMarkerName,
                SchemaVersion = ProviderInstanceSchema.CurrentVersion,
                IsReady = isReady,
                UpdatedUtc = now
            };
            dbContext.ReadinessMarkers.Add(marker);
        }
        else
        {
            marker.SchemaVersion = ProviderInstanceSchema.CurrentVersion;
            marker.IsReady = isReady;
            marker.UpdatedUtc = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

public sealed record CredentialGrantSummary(
    CredentialGrantScope Scope,
    long? TelegramPrincipalId);

public sealed record CredentialSummary(
    int Id,
    Guid StableId,
    SearchProviderEnum ProviderKind,
    CredentialSource Source,
    bool IsEnabled,
    DateTime? LastClaimedUtc,
    DateTime? LastUsedUtc,
    DateTime? CooldownUntilUtc,
    string? DisabledReason,
    DateTime? DisabledAtUtc,
    IReadOnlyList<CredentialGrantSummary> Grants)
{
    public string Alias => StableId == Guid.Empty
        ? "unassigned"
        : StableId.ToString("N")[..12];
}

public sealed record CredentialWriteResult(
    bool Created,
    int Id,
    Guid StableId)
{
    public string Alias => StableId == Guid.Empty
        ? "unassigned"
        : StableId.ToString("N")[..12];
}
