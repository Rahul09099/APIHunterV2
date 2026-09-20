using System.Reflection;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Tests.Infrastructure;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Expected-red persistence and state-transition contract for Task 4.1.
///
/// **Validates: Requirements 3.1-3.4, 3.16, 3.18-3.19, 3.33-3.38**
/// </summary>
public sealed class CredentialStateInvariantPropertyTests
{
    /// <summary>
    /// **Validates: Requirements 3.1-3.4, 3.6, 3.16, 3.18**
    /// </summary>
    [Fact]
    public void SearchProviderToken_RemainsPhysicalIdentityWithProtectedVersionedFields()
    {
        using var context = new DBContext(
            new DbContextOptionsBuilder<DBContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);
        var entity = CredentialProtectionTestContract.RequireCredentialEntity(context.Model);

        var stableId = CredentialProtectionTestContract.RequireSemanticProperty(
            entity,
            "an immutable credential Stable ID",
            nullable: false,
            [typeof(Guid)],
            "StableId");
        CredentialProtectionTestContract.RequireSemanticProperty(
            entity,
            "one required Provider Instance relationship",
            nullable: false,
            [typeof(long)],
            "ProviderInstanceId");
        CredentialProtectionTestContract.RequireSemanticProperty(
            entity,
            "the protection-envelope format version",
            nullable: false,
            [typeof(int)],
            "EnvelopeFormatVersion",
            "FormatVersion",
            "EnvelopeVersion");
        CredentialProtectionTestContract.RequireSemanticProperty(
            entity,
            "the protection-key version",
            nullable: false,
            [typeof(int)],
            "ProtectionKeyVersion");
        CredentialProtectionTestContract.RequireSemanticProperty(
            entity,
            "the AES-GCM nonce",
            nullable: false,
            [typeof(byte[]), typeof(string)],
            "ProtectionNonce",
            "Nonce");
        CredentialProtectionTestContract.RequireSemanticProperty(
            entity,
            "the protected ciphertext",
            nullable: false,
            [typeof(byte[]), typeof(string)],
            "ProtectedCiphertext",
            "Ciphertext");
        CredentialProtectionTestContract.RequireSemanticProperty(
            entity,
            "the AES-GCM authentication tag",
            nullable: false,
            [typeof(byte[]), typeof(string)],
            "AuthenticationTag",
            "ProtectionTag",
            "Tag");
        CredentialProtectionTestContract.RequireSemanticProperty(
            entity,
            "the versioned HMAC fingerprint",
            nullable: false,
            [typeof(byte[]), typeof(string)],
            "Fingerprint");
        CredentialProtectionTestContract.RequireSemanticProperty(
            entity,
            "the fingerprint-key version",
            nullable: false,
            [typeof(int)],
            "FingerprintKeyVersion");
        CredentialProtectionTestContract.RequireSemanticProperty(
            entity,
            "the disabled reason",
            nullable: true,
            [typeof(string)],
            "DisabledReason");
        CredentialProtectionTestContract.RequireSemanticProperty(
            entity,
            "the disabled UTC time",
            nullable: true,
            [typeof(DateTime?)],
            "DisabledAtUtc",
            "DisabledAtUTC");

        Assert.Equal(PropertySaveBehavior.Throw, stableId.GetAfterSaveBehavior());
        var relationship = entity.GetForeignKeys().SingleOrDefault(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(["ProviderInstanceId"]));
        Assert.NotNull(relationship);
        Assert.True(relationship!.IsRequired);

        var forbiddenConfigurationContractColumns = entity.GetProperties()
            .Select(property => property.Name)
            .Where(name =>
                name.Contains("ConfigurationKey", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("EnvironmentVariable", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("KeyName", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(forbiddenConfigurationContractColumns);
    }

    /// <summary>
    /// **Validates: Requirements 3.33-3.36**
    /// </summary>
    [Fact]
    public void CredentialSchema_EnforcesEnabledAndDisabledStateInvariant()
    {
        using var context = new DBContext(
            new DbContextOptionsBuilder<DBContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);
        var designTimeModel = context.GetService<IDesignTimeModel>().Model;
        var entity = CredentialProtectionTestContract.RequireCredentialEntity(designTimeModel);
        var stateChecks = entity.GetCheckConstraints()
            .Select(check => check.Sql)
            .Where(sql => !string.IsNullOrWhiteSpace(sql))
            .Cast<string>()
            .Where(sql =>
                sql.Contains("IsEnabled", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("DisabledReason", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("DisabledAt", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            stateChecks.Length > 0,
            "SearchProviderTokens must enforce enabled=(reason/time null) and disabled=(reason/time non-null) at the database boundary.");
    }

    /// <summary>
    /// **Validates: Requirements 3.33**
    /// </summary>
    [Property(MaxTest = 40)]
    public void EnableTransition_AlwaysClearsDisabledReasonAndTime(NonNegativeInt input)
    {
        var service = CredentialProtectionTestContract.CreateStateService();
        var credential = new SearchProviderToken { IsEnabled = false };
        var priorTime = DateTime.UnixEpoch.AddSeconds(input.Get + 1).ToUniversalTime();
        CredentialProtectionTestContract.SetPublicProperty(
            credential,
            "PriorDisabledReason",
            "DisabledReason");
        CredentialProtectionTestContract.SetPublicProperty(
            credential,
            priorTime,
            "DisabledAtUtc",
            "DisabledAtUTC");

        CredentialProtectionTestContract.EnableCredential(service, credential);

        Assert.True(credential.IsEnabled);
        Assert.Null(CredentialProtectionTestContract.ReadPublicProperty(credential, "DisabledReason"));
        Assert.Null(CredentialProtectionTestContract.ReadPublicProperty(
            credential,
            "DisabledAtUtc",
            "DisabledAtUTC"));
    }

    /// <summary>
    /// **Validates: Requirements 3.34, 3.35, 3.36**
    /// </summary>
    [Property(MaxTest = 50)]
    public void DisableTransition_AlwaysSetsReasonAndDatabaseUtcTime(NonNegativeInt input)
    {
        var service = CredentialProtectionTestContract.CreateStateService();
        var credential = new SearchProviderToken { IsEnabled = true };
        var reason = $"Task41DisabledReason{input.Get}";
        var disabledAtUtc = DateTime.SpecifyKind(
            DateTime.UnixEpoch.AddSeconds(input.Get + 1),
            DateTimeKind.Utc);

        CredentialProtectionTestContract.DisableCredential(
            service,
            credential,
            reason,
            disabledAtUtc);

        Assert.False(credential.IsEnabled);
        var persistedReason = Assert.IsType<string>(
            CredentialProtectionTestContract.ReadPublicProperty(credential, "DisabledReason"));
        Assert.False(string.IsNullOrWhiteSpace(persistedReason));
        Assert.Equal(reason, persistedReason);
        var persistedTime = Assert.IsType<DateTime>(
            CredentialProtectionTestContract.ReadPublicProperty(
                credential,
                "DisabledAtUtc",
                "DisabledAtUTC"));
        Assert.Equal(DateTimeKind.Utc, persistedTime.Kind);
        Assert.Equal(disabledAtUtc, persistedTime);
    }

    /// <summary>
    /// **Validates: Requirements 3.21-3.24, 3.34-3.36, 3.38**
    /// </summary>
    [Fact]
    public void ProtectionQuarantine_IsDisabledWithProtectionFailureAndIsNotAProviderOutcome()
    {
        var service = CredentialProtectionTestContract.CreateStateService();
        var credential = new SearchProviderToken { IsEnabled = true };
        var disabledAtUtc = DateTime.SpecifyKind(
            new DateTime(2031, 4, 17, 10, 30, 0),
            DateTimeKind.Utc);

        CredentialProtectionTestContract.DisableCredential(
            service,
            credential,
            "ProtectionFailure",
            disabledAtUtc);

        Assert.False(credential.IsEnabled);
        Assert.Equal(
            "ProtectionFailure",
            CredentialProtectionTestContract.ReadPublicProperty(credential, "DisabledReason"));
        Assert.Equal(
            disabledAtUtc,
            CredentialProtectionTestContract.ReadPublicProperty(
                credential,
                "DisabledAtUtc",
                "DisabledAtUTC"));
        Assert.All(
            CredentialProtectionTestContract.FindOutcomeEnums(),
            outcomeType => Assert.DoesNotContain(
                "ProtectionFailure",
                Enum.GetNames(outcomeType),
                StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// **Validates: Requirements 3.19**
    /// </summary>
    [Fact]
    public void ReadinessContract_RequiresProtectionAndFingerprintKeyHealthForEnabledCredentials()
    {
        var keyReadinessType = CredentialProtectionTestContract.RequireConcreteTypeForContract(
            "CredentialProtectionReadinessService");
        Assert.Contains(
            keyReadinessType.GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method.Name.Contains("Evaluate", StringComparison.OrdinalIgnoreCase) ||
                      method.Name.Contains("Readiness", StringComparison.OrdinalIgnoreCase));

        var readinessConstructors = typeof(ProviderInstanceReadinessService).GetConstructors();
        Assert.Contains(
            readinessConstructors.SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == keyReadinessType ||
                         parameter.ParameterType.Name.Contains("CredentialProtectionReadiness", StringComparison.OrdinalIgnoreCase));

        var readinessReportProperties = typeof(ProviderInstanceReadinessReport)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);
        Assert.Contains(
            readinessReportProperties,
            property => property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// **Validates: Requirements 3.27, 3.28, 17.47**
    /// </summary>
    [Fact]
    public void DecryptionContract_IsSingleCredentialCurrentOperationScoped()
    {
        CredentialProtectionTestContract.AssertDecryptApiIsSingleCredentialScoped();
    }
}
