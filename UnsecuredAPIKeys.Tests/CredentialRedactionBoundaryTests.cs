using System.Text;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Tests.Infrastructure;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Expected-red control-plane secret non-disclosure contract for Task 4.1.
/// Discovered-secret access/export behavior is intentionally outside this suite.
///
/// **Validates: Requirements 3.17, 3.27-3.32, 3.37, 17.8, 17.47**
/// </summary>
public sealed class CredentialRedactionBoundaryTests
{
    private const int ProtectionKeyVersion = 7;
    private const int FingerprintKeyVersion = 11;

    /// <summary>
    /// **Validates: Requirements 3.17, 3.37, 17.8**
    /// </summary>
    [Property(MaxTest = 40)]
    public void TelemetryRedactor_RemovesCredentialNodeTokenAuthorizationAndFullFingerprint(
        NonNegativeInt input)
    {
        var credential = CredentialProtectionTestContract.CreateCredentialMaterial(input.Get);
        var nodeToken = $"node-task-4.1-{Math.Abs((long)input.Get):x16}";
        var fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes($"fingerprint-{input.Get}")))
            .ToLowerInvariant();
        var rendered =
            $"credential={credential}; X-Node-Token: {nodeToken}; " +
            $"Authorization: Bearer {credential}; Fingerprint={fingerprint}";

        var redacted = CredentialProtectionTestContract.Redact(rendered);

        Assert.DoesNotContain(credential, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(nodeToken, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(fingerprint, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer " + credential, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// **Validates: Requirements 3.27-3.30, 17.47**
    /// </summary>
    [Property(MaxTest = 35)]
    public void ProtectedOperation_RetainsNoPlaintextInTrackedEntitiesOrLongLivedServiceState(
        NonNegativeInt input)
    {
        var material = CredentialProtectionTestContract.CreateCredentialMaterial(input.Get);
        var context = CredentialProtectionTestContract.CreateContext(input.Get);
        var keys = CredentialProtectionTestContract.CreateKeys(ProtectionKeyVersion);
        var service = CredentialProtectionTestContract.CreateProtectionService(
            keys,
            ProtectionKeyVersion);
        var envelope = CredentialProtectionTestContract.Protect(service, material, context);
        var credential = new SearchProviderToken();
        CredentialProtectionTestContract.PopulateProtectedCredential(
            credential,
            envelope,
            context);

        using var dbContext = new DBContext(
            new DbContextOptionsBuilder<DBContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);
        dbContext.Attach(credential);

        var trackedValues = dbContext.Entry(credential).Properties
            .Select(property => property.CurrentValue)
            .Where(value => value is not null)
            .Select(value => value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                _ => value!.ToString() ?? string.Empty
            })
            .ToArray();
        Assert.DoesNotContain(
            trackedValues,
            value => value.Contains(material, StringComparison.Ordinal));

        Assert.Equal(material, CredentialProtectionTestContract.Unprotect(service, envelope, context));
        CredentialProtectionTestContract.AssertNoPlaintextRetained(service, material);
    }

    /// <summary>
    /// **Validates: Requirements 3.31, 17.8**
    /// </summary>
    [Fact]
    public void ControlPlaneSecrets_AreNeverAcceptedFromOrPlacedIntoQueryStrings()
    {
        var violations = CredentialProtectionTestContract.FindControlPlaneSecretQueryStringViolations();
        Assert.True(
            violations.Count == 0,
            "Control-plane secret query-string paths were found:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// **Validates: Requirements 3.17, 3.29-3.31, 3.37, 17.8**
    /// </summary>
    [Fact]
    public void NonClaimDtos_ExposeNoCredentialNodeTokenOrFullFingerprintMembers()
    {
        var violations = CredentialProtectionTestContract.FindNonClaimDtoSecretMembers();
        Assert.True(
            violations.Count == 0,
            "Non-Claim DTOs expose control-plane secret members:\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// **Validates: Requirements 3.17, 3.27-3.30, 17.8, 17.36, 17.47**
    /// </summary>
    [Fact]
    public void ProtectionFailures_DoNotLeakPlaintextKeysOrFullFingerprintThroughExceptions()
    {
        const string material = "ghp_task_4_1_exception_disclosure_canary";
        var context = CredentialProtectionTestContract.CreateContext(1708);
        var protectionKeys = CredentialProtectionTestContract.CreateKeys(ProtectionKeyVersion);
        var writer = CredentialProtectionTestContract.CreateProtectionService(
            protectionKeys,
            ProtectionKeyVersion);
        var envelope = CredentialProtectionTestContract.Protect(writer, material, context);
        var fingerprintService = CredentialProtectionTestContract.CreateFingerprintService(
            CredentialProtectionTestContract.CreateKeys(FingerprintKeyVersion),
            FingerprintKeyVersion);
        var fingerprint = CredentialProtectionTestContract.ComputeFingerprint(
            fingerprintService,
            material,
            context,
            FingerprintKeyVersion);
        var wrongKey = CredentialProtectionTestContract.CreateKeys(99)[99];
        var wrongReader = CredentialProtectionTestContract.CreateProtectionService(
            new Dictionary<int, byte[]> { [ProtectionKeyVersion] = wrongKey },
            ProtectionKeyVersion);

        var error = Record.Exception(() =>
            CredentialProtectionTestContract.Unprotect(wrongReader, envelope, context));

        Assert.NotNull(error);
        var rendered = error!.ToString();
        Assert.DoesNotContain(material, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(protectionKeys[ProtectionKeyVersion]), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(wrongKey), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(fingerprint.Bytes), rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(fingerprint.Bytes), rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// **Validates: Requirements 3.17, 3.18, 3.32, 3.37**
    /// </summary>
    [Fact]
    public void ProtectedCredentialStorageContract_UsesVersionsNotDeploymentKeyNames()
    {
        using var context = new DBContext(
            new DbContextOptionsBuilder<DBContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);
        var entity = CredentialProtectionTestContract.RequireCredentialEntity(context.Model);
        var propertyNames = entity.GetProperties().Select(property => property.Name).ToArray();

        Assert.Contains("ProtectionKeyVersion", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("FingerprintKeyVersion", propertyNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            propertyNames,
            name => name.Contains("Environment", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("ConfigurationKey", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("KeyName", StringComparison.OrdinalIgnoreCase));
    }
}
