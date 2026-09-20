using System.Text;
using FsCheck;
using FsCheck.Xunit;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Tests.Infrastructure;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Expected-red cryptographic contract for multi-search-provider-platform Task 4.1.
/// Production protection and fingerprint services are intentionally introduced by Task 4.2.
///
/// **Validates: Requirements 3.4-3.18, 17.35-17.36**
/// </summary>
public sealed class CredentialProtectionPropertyTests
{
    private const int PreviousProtectionKeyVersion = 6;
    private const int ActiveProtectionKeyVersion = 7;
    private const int ActiveFingerprintKeyVersion = 11;

    /// <summary>
    /// **Validates: Requirements 3.4-3.6, 3.8-3.10, 3.13, 17.35**
    /// </summary>
    [Property(MaxTest = 50)]
    public void Aes256GcmEnvelope_RoundTripsArbitraryCredentialMaterial(NonNegativeInt input)
    {
        var context = CredentialProtectionTestContract.CreateContext(input.Get);
        var material = CredentialProtectionTestContract.CreateCredentialMaterial(input.Get);
        var keys = CredentialProtectionTestContract.CreateKeys(
            PreviousProtectionKeyVersion,
            ActiveProtectionKeyVersion);
        var service = CredentialProtectionTestContract.CreateProtectionService(
            keys,
            ActiveProtectionKeyVersion);

        var envelope = CredentialProtectionTestContract.Protect(service, material, context);
        var decrypted = CredentialProtectionTestContract.Unprotect(service, envelope, context);

        Assert.Equal(material, decrypted);
        Assert.True(CredentialProtectionTestContract.ReadEnvelopeFormatVersion(envelope) > 0);
        Assert.Equal(
            ActiveProtectionKeyVersion,
            CredentialProtectionTestContract.ReadProtectionKeyVersion(envelope));
        Assert.Equal(12, CredentialProtectionTestContract.ReadNonce(envelope).Length);
        Assert.Equal(16, CredentialProtectionTestContract.ReadAuthenticationTag(envelope).Length);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(material),
            CredentialProtectionTestContract.ReadCiphertext(envelope).Length);
        Assert.DoesNotContain(
            material,
            Encoding.UTF8.GetString(CredentialProtectionTestContract.ReadCiphertext(envelope)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// **Validates: Requirements 3.5, 3.7**
    /// </summary>
    [Property(MaxTest = 40)]
    public void RepeatedProtection_UsesFreshNinetySixBitNonces(NonNegativeInt input)
    {
        var context = CredentialProtectionTestContract.CreateContext(input.Get);
        var material = CredentialProtectionTestContract.CreateCredentialMaterial(input.Get);
        var service = CredentialProtectionTestContract.CreateProtectionService(
            CredentialProtectionTestContract.CreateKeys(ActiveProtectionKeyVersion),
            ActiveProtectionKeyVersion);

        var first = CredentialProtectionTestContract.Protect(service, material, context);
        var second = CredentialProtectionTestContract.Protect(service, material, context);

        var firstNonce = CredentialProtectionTestContract.ReadNonce(first);
        var secondNonce = CredentialProtectionTestContract.ReadNonce(second);
        Assert.Equal(12, firstNonce.Length);
        Assert.Equal(12, secondNonce.Length);
        Assert.False(firstNonce.SequenceEqual(secondNonce), "Each AES-GCM envelope must use a fresh random nonce.");
    }

    /// <summary>
    /// **Validates: Requirements 3.8, 3.9, 3.10, 17.36**
    /// </summary>
    [Property(MaxTest = 45)]
    public void AssociatedData_BindsCredentialKindAndProviderInstance(
        NonNegativeInt input,
        NonNegativeInt changedField)
    {
        var context = CredentialProtectionTestContract.CreateContext(input.Get);
        var material = CredentialProtectionTestContract.CreateCredentialMaterial(input.Get);
        var keys = CredentialProtectionTestContract.CreateKeys(ActiveProtectionKeyVersion);
        var service = CredentialProtectionTestContract.CreateProtectionService(
            keys,
            ActiveProtectionKeyVersion);
        var envelope = CredentialProtectionTestContract.Protect(service, material, context);

        var mismatchedContext = (Math.Abs((long)changedField.Get) % 3) switch
        {
            0 => context with { CredentialStableId = Guid.NewGuid() },
            1 => context with
            {
                ProviderKind = context.ProviderKind == SearchProviderEnum.GitHub
                    ? SearchProviderEnum.GitLab
                    : SearchProviderEnum.GitHub
            },
            _ => context with { ProviderInstanceStableId = Guid.NewGuid() }
        };

        AssertRejectedWithoutDisclosure(
            () => CredentialProtectionTestContract.Unprotect(service, envelope, mismatchedContext),
            material,
            keys.Values);
    }

    /// <summary>
    /// **Validates: Requirements 3.11, 3.12, 3.13, 17.35**
    /// </summary>
    [Fact]
    public void ActiveAndPreviousProtectionKeys_DecryptWhileNewEnvelopesUseOnlyActiveVersion()
    {
        const string material = "ghp_task_4_1_active_previous_key_canary";
        var context = CredentialProtectionTestContract.CreateContext(1735);
        var keys = CredentialProtectionTestContract.CreateKeys(
            PreviousProtectionKeyVersion,
            ActiveProtectionKeyVersion);
        var previousWriter = CredentialProtectionTestContract.CreateProtectionService(
            keys,
            PreviousProtectionKeyVersion);
        var rotatingReaderWriter = CredentialProtectionTestContract.CreateProtectionService(
            keys,
            ActiveProtectionKeyVersion);

        var previousEnvelope = CredentialProtectionTestContract.Protect(
            previousWriter,
            material,
            context);
        var activeEnvelope = CredentialProtectionTestContract.Protect(
            rotatingReaderWriter,
            material,
            context);

        Assert.Equal(
            PreviousProtectionKeyVersion,
            CredentialProtectionTestContract.ReadProtectionKeyVersion(previousEnvelope));
        Assert.Equal(
            ActiveProtectionKeyVersion,
            CredentialProtectionTestContract.ReadProtectionKeyVersion(activeEnvelope));
        Assert.Equal(
            material,
            CredentialProtectionTestContract.Unprotect(
                rotatingReaderWriter,
                previousEnvelope,
                context));
        Assert.Equal(
            material,
            CredentialProtectionTestContract.Unprotect(
                rotatingReaderWriter,
                activeEnvelope,
                context));
    }

    /// <summary>
    /// **Validates: Requirements 3.5, 17.36**
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(31)]
    [InlineData(33)]
    public void ProtectionService_RejectsKeysThatAreNotExactly256Bits(int keyLength)
    {
        var keys = new Dictionary<int, byte[]> { [ActiveProtectionKeyVersion] = new byte[keyLength] };
        var context = CredentialProtectionTestContract.CreateContext(keyLength);
        const string material = "task-4.1-invalid-key-length-canary";

        AssertRejectedWithoutDisclosure(
            () =>
            {
                var service = CredentialProtectionTestContract.CreateProtectionService(
                    keys,
                    ActiveProtectionKeyVersion);
                CredentialProtectionTestContract.Protect(service, material, context);
            },
            material,
            keys.Values);
    }

    /// <summary>
    /// **Validates: Requirements 3.6, 3.11-3.13, 17.36**
    /// </summary>
    [Fact]
    public void MissingAndWrongProtectionKeys_FailClosedWithoutCredentialDisclosure()
    {
        const string material = "glpat-task-4.1-missing-wrong-key-canary";
        var context = CredentialProtectionTestContract.CreateContext(1736);
        var originalKeys = CredentialProtectionTestContract.CreateKeys(PreviousProtectionKeyVersion);
        var writer = CredentialProtectionTestContract.CreateProtectionService(
            originalKeys,
            PreviousProtectionKeyVersion);
        var envelope = CredentialProtectionTestContract.Protect(writer, material, context);

        var missingVersionKeys = CredentialProtectionTestContract.CreateKeys(ActiveProtectionKeyVersion);
        var missingVersionReader = CredentialProtectionTestContract.CreateProtectionService(
            missingVersionKeys,
            ActiveProtectionKeyVersion);
        AssertRejectedWithoutDisclosure(
            () => CredentialProtectionTestContract.Unprotect(
                missingVersionReader,
                envelope,
                context),
            material,
            originalKeys.Values.Concat(missingVersionKeys.Values));

        var wrongKey = CredentialProtectionTestContract.CreateKeys(99)[99];
        var wrongVersionMatchedKeys = new Dictionary<int, byte[]>
        {
            [PreviousProtectionKeyVersion] = wrongKey
        };
        var wrongKeyReader = CredentialProtectionTestContract.CreateProtectionService(
            wrongVersionMatchedKeys,
            PreviousProtectionKeyVersion);
        AssertRejectedWithoutDisclosure(
            () => CredentialProtectionTestContract.Unprotect(wrongKeyReader, envelope, context),
            material,
            originalKeys.Values.Concat(wrongVersionMatchedKeys.Values));
    }

    /// <summary>
    /// **Validates: Requirements 3.6, 3.8-3.10, 17.36**
    /// </summary>
    [Theory]
    [InlineData("nonce")]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("format-version")]
    [InlineData("key-version")]
    public void TamperedEnvelopeMaterial_FailsAuthenticationWithoutDisclosure(string tamperTarget)
    {
        const string material = "ghp_task_4_1_tamper_canary";
        var context = CredentialProtectionTestContract.CreateContext(41736);
        var keys = CredentialProtectionTestContract.CreateKeys(ActiveProtectionKeyVersion);
        var service = CredentialProtectionTestContract.CreateProtectionService(
            keys,
            ActiveProtectionKeyVersion);
        var envelope = CredentialProtectionTestContract.Protect(service, material, context);

        var tampered = tamperTarget switch
        {
            "nonce" => CredentialProtectionTestContract.TamperBinary(
                envelope,
                "Nonce",
                "ProtectionNonce"),
            "ciphertext" => CredentialProtectionTestContract.TamperBinary(
                envelope,
                "Ciphertext",
                "CipherText",
                "ProtectedCiphertext",
                "EncryptedMaterial"),
            "tag" => CredentialProtectionTestContract.TamperBinary(
                envelope,
                "AuthenticationTag",
                "Tag",
                "ProtectionTag"),
            "format-version" => CredentialProtectionTestContract.TamperInteger(
                envelope,
                int.MaxValue,
                "EnvelopeFormatVersion",
                "FormatVersion",
                "EnvelopeVersion"),
            "key-version" => CredentialProtectionTestContract.TamperInteger(
                envelope,
                int.MaxValue,
                "ProtectionKeyVersion",
                "KeyVersion"),
            _ => throw new ArgumentOutOfRangeException(nameof(tamperTarget))
        };

        AssertRejectedWithoutDisclosure(
            () => CredentialProtectionTestContract.Unprotect(service, tampered, context),
            material,
            keys.Values);
    }

    /// <summary>
    /// **Validates: Requirements 3.14, 3.15, 3.16**
    /// </summary>
    [Property(MaxTest = 50)]
    public void Fingerprint_IsDeterministicAndBoundToProviderInstanceKindAndMaterial(
        NonNegativeInt input)
    {
        var context = CredentialProtectionTestContract.CreateContext(input.Get);
        var material = CredentialProtectionTestContract.CreateCredentialMaterial(input.Get);
        var keys = CredentialProtectionTestContract.CreateKeys(ActiveFingerprintKeyVersion);
        var service = CredentialProtectionTestContract.CreateFingerprintService(
            keys,
            ActiveFingerprintKeyVersion);

        var first = CredentialProtectionTestContract.ComputeFingerprint(
            service,
            material,
            context,
            ActiveFingerprintKeyVersion);
        var replay = CredentialProtectionTestContract.ComputeFingerprint(
            service,
            material,
            context,
            ActiveFingerprintKeyVersion);
        var otherMaterial = CredentialProtectionTestContract.ComputeFingerprint(
            service,
            material + "-changed",
            context,
            ActiveFingerprintKeyVersion);
        var otherInstance = CredentialProtectionTestContract.ComputeFingerprint(
            service,
            material,
            context with { ProviderInstanceStableId = Guid.NewGuid() },
            ActiveFingerprintKeyVersion);
        var otherKind = CredentialProtectionTestContract.ComputeFingerprint(
            service,
            material,
            context with
            {
                ProviderKind = context.ProviderKind == SearchProviderEnum.GitHub
                    ? SearchProviderEnum.GitLab
                    : SearchProviderEnum.GitHub
            },
            ActiveFingerprintKeyVersion);

        Assert.Equal(ActiveFingerprintKeyVersion, first.KeyVersion);
        Assert.Equal(32, first.Bytes.Length);
        Assert.True(first.Bytes.SequenceEqual(replay.Bytes));
        Assert.False(first.Bytes.SequenceEqual(otherMaterial.Bytes));
        Assert.False(first.Bytes.SequenceEqual(otherInstance.Bytes));
        Assert.False(first.Bytes.SequenceEqual(otherKind.Bytes));
    }

    /// <summary>
    /// **Validates: Requirements 3.14, 3.15**
    /// </summary>
    [Fact]
    public void FingerprintUsesDedicatedHmacSha256KeyMaterialSeparateFromProtectionKeys()
    {
        var protectionType = CredentialProtectionTestContract.RequireConcreteTypeForContract(
            "CredentialProtectionService");
        var fingerprintType = CredentialProtectionTestContract.RequireConcreteTypeForContract(
            "CredentialFingerprintService");
        Assert.NotEqual(protectionType, fingerprintType);

        var context = CredentialProtectionTestContract.CreateContext(31415);
        const string material = "task-4.1-separate-hmac-key-canary";
        var firstFingerprintKeys = CredentialProtectionTestContract.CreateKeys(ActiveFingerprintKeyVersion);
        var secondFingerprintKey = CredentialProtectionTestContract.CreateKeys(77)[77];
        var firstService = CredentialProtectionTestContract.CreateFingerprintService(
            firstFingerprintKeys,
            ActiveFingerprintKeyVersion);
        var secondService = CredentialProtectionTestContract.CreateFingerprintService(
            new Dictionary<int, byte[]> { [ActiveFingerprintKeyVersion] = secondFingerprintKey },
            ActiveFingerprintKeyVersion);

        var first = CredentialProtectionTestContract.ComputeFingerprint(
            firstService,
            material,
            context,
            ActiveFingerprintKeyVersion);
        var second = CredentialProtectionTestContract.ComputeFingerprint(
            secondService,
            material,
            context,
            ActiveFingerprintKeyVersion);
        Assert.False(first.Bytes.SequenceEqual(second.Bytes));

        var serviceSources = string.Join("\n", CredentialProtectionTestContract.ReadProtectionPrimitiveSources());
        Assert.Contains("AesGcm", serviceSources, StringComparison.Ordinal);
        Assert.Contains("HMACSHA256", serviceSources, StringComparison.Ordinal);
    }

    private static void AssertRejectedWithoutDisclosure(
        Action action,
        string material,
        IEnumerable<byte[]> sensitiveKeys)
    {
        var error = Record.Exception(action);
        Assert.NotNull(error);
        var rendered = error!.ToString();
        Assert.DoesNotContain(material, rendered, StringComparison.Ordinal);
        foreach (var key in sensitiveKeys)
        {
            Assert.DoesNotContain(Convert.ToBase64String(key), rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToHexString(key), rendered, StringComparison.OrdinalIgnoreCase);
        }
    }
}
