using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Services;

public sealed record CredentialProtectionContext(
    Guid CredentialStableId,
    SearchProviderEnum ProviderKind,
    Guid ProviderInstanceStableId);

public sealed class CredentialProtectionEnvelope
{
    public int EnvelopeFormatVersion { get; set; }
    public int ProtectionKeyVersion { get; set; }
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] AuthenticationTag { get; set; } = [];
}

public sealed class CredentialFingerprint
{
    public int FingerprintKeyVersion { get; set; }
    public byte[] Fingerprint { get; set; } = [];
}

public sealed class CredentialProtectionException : Exception
{
    public CredentialProtectionException(string message) : base(message)
    {
    }
}

/// <summary>
/// AES-256-GCM protection with versioned deployment-supplied keys. The service never
/// retains plaintext and exposes only one-envelope-at-a-time decryption methods.
/// </summary>
public sealed class CredentialProtectionService
{
    public const int CurrentEnvelopeFormatVersion = 1;
    public const int NonceSizeBytes = 12;
    public const int AuthenticationTagSizeBytes = 16;
    private const int Aes256KeySizeBytes = 32;

    private readonly VersionedCredentialKeyRing _keyRing;

    public CredentialProtectionService(
        IReadOnlyDictionary<int, byte[]> keys,
        int activeVersion)
    {
        _keyRing = VersionedCredentialKeyRing.FromExplicit(
            keys,
            activeVersion,
            Aes256KeySizeBytes,
            "protection");
    }

    public CredentialProtectionService(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _keyRing = VersionedCredentialKeyRing.FromConfiguration(
            configuration,
            "Protection",
            Aes256KeySizeBytes);
    }

    public int ActiveKeyVersion => _keyRing.ActiveVersion;
    public bool IsConfigured => _keyRing.IsConfigured;

    public bool HasKeyVersion(int keyVersion) => _keyRing.HasVersion(keyVersion);

    public CredentialProtectionEnvelope Protect(
        string material,
        CredentialProtectionContext context)
    {
        ValidateContext(context);
        var plaintext = EncodeMaterial(material);
        var key = _keyRing.RequireActiveKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AuthenticationTagSizeBytes];
        var associatedData = BuildAssociatedData(context);

        try
        {
            using var aes = new AesGcm(key, AuthenticationTagSizeBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            return new CredentialProtectionEnvelope
            {
                EnvelopeFormatVersion = CurrentEnvelopeFormatVersion,
                ProtectionKeyVersion = _keyRing.ActiveVersion,
                Nonce = nonce,
                Ciphertext = ciphertext,
                AuthenticationTag = tag
            };
        }
        catch (CredentialProtectionException)
        {
            throw;
        }
        catch
        {
            throw new CredentialProtectionException("Credential protection failed closed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    public string Unprotect(
        CredentialProtectionEnvelope envelope,
        CredentialProtectionContext context)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateContext(context);
        ValidateEnvelope(envelope);

        var key = _keyRing.RequireKey(envelope.ProtectionKeyVersion);
        var plaintext = new byte[envelope.Ciphertext.Length];
        var associatedData = BuildAssociatedData(context);
        try
        {
            using var aes = new AesGcm(key, AuthenticationTagSizeBytes);
            aes.Decrypt(
                envelope.Nonce,
                envelope.Ciphertext,
                envelope.AuthenticationTag,
                plaintext,
                associatedData);
            return new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(plaintext);
        }
        catch
        {
            throw new CredentialProtectionException(
                "Credential envelope authentication or decryption failed closed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    internal bool UsesSameActiveKeyMaterial(CredentialFingerprintService fingerprintService)
    {
        ArgumentNullException.ThrowIfNull(fingerprintService);
        return fingerprintService.ActiveKeyEquals(_keyRing);
    }

    private static void ValidateEnvelope(CredentialProtectionEnvelope envelope)
    {
        if (envelope.EnvelopeFormatVersion != CurrentEnvelopeFormatVersion ||
            envelope.ProtectionKeyVersion <= 0 ||
            envelope.Nonce is null ||
            envelope.Nonce.Length != NonceSizeBytes ||
            envelope.Ciphertext is null ||
            envelope.Ciphertext.Length == 0 ||
            envelope.AuthenticationTag is null ||
            envelope.AuthenticationTag.Length != AuthenticationTagSizeBytes)
        {
            throw new CredentialProtectionException("Credential envelope is invalid or unsupported.");
        }
    }

    private static void ValidateContext(CredentialProtectionContext context)
    {
        if (context.CredentialStableId == Guid.Empty ||
            context.ProviderInstanceStableId == Guid.Empty ||
            context.ProviderKind is not SearchProviderEnum.GitHub and not SearchProviderEnum.GitLab)
        {
            throw new CredentialProtectionException("Credential protection identity is invalid.");
        }
    }

    private static byte[] EncodeMaterial(string material)
    {
        if (string.IsNullOrWhiteSpace(material))
        {
            throw new CredentialProtectionException("Credential material is required.");
        }

        return Encoding.UTF8.GetBytes(material);
    }

    private static byte[] BuildAssociatedData(CredentialProtectionContext context)
    {
        var domain = Encoding.ASCII.GetBytes("APIHunterV2/CredentialEnvelope/v1");
        var associatedData = new byte[domain.Length + 16 + sizeof(int) + 16];
        domain.CopyTo(associatedData, 0);
        context.CredentialStableId.TryWriteBytes(associatedData.AsSpan(domain.Length, 16));
        BinaryPrimitives.WriteInt32BigEndian(
            associatedData.AsSpan(domain.Length + 16, sizeof(int)),
            (int)context.ProviderKind);
        context.ProviderInstanceStableId.TryWriteBytes(
            associatedData.AsSpan(domain.Length + 16 + sizeof(int), 16));
        return associatedData;
    }
}

/// <summary>
/// Versioned HMAC-SHA-256 credential identity. It is intentionally separate from
/// <see cref="CredentialProtectionService"/> and uses an independent key ring.
/// </summary>
public sealed class CredentialFingerprintService
{
    private const int HmacSha256KeySizeBytes = 32;
    private readonly VersionedCredentialKeyRing _keyRing;

    public CredentialFingerprintService(
        IReadOnlyDictionary<int, byte[]> keys,
        int activeVersion)
    {
        _keyRing = VersionedCredentialKeyRing.FromExplicit(
            keys,
            activeVersion,
            HmacSha256KeySizeBytes,
            "fingerprint");
    }

    public CredentialFingerprintService(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _keyRing = VersionedCredentialKeyRing.FromConfiguration(
            configuration,
            "Fingerprint",
            HmacSha256KeySizeBytes);
    }

    public int ActiveKeyVersion => _keyRing.ActiveVersion;
    public bool IsConfigured => _keyRing.IsConfigured;

    public bool HasKeyVersion(int keyVersion) => _keyRing.HasVersion(keyVersion);

    public CredentialFingerprint ComputeFingerprint(
        string material,
        CredentialProtectionContext context) =>
        ComputeWithKeyVersion(material, context, _keyRing.ActiveVersion);

    internal CredentialFingerprint ComputeWithKeyVersion(
        string material,
        CredentialProtectionContext context,
        int keyVersion)
    {
        if (context.CredentialStableId == Guid.Empty ||
            context.ProviderInstanceStableId == Guid.Empty ||
            context.ProviderKind is not SearchProviderEnum.GitHub and not SearchProviderEnum.GitLab)
        {
            throw new CredentialProtectionException("Credential fingerprint identity is invalid.");
        }
        if (string.IsNullOrWhiteSpace(material))
        {
            throw new CredentialProtectionException("Credential material is required.");
        }

        var key = _keyRing.RequireKey(keyVersion);
        var materialBytes = Encoding.UTF8.GetBytes(material);
        var input = BuildFingerprintInput(context, materialBytes);
        try
        {
            using var hmac = new HMACSHA256(key);
            return new CredentialFingerprint
            {
                FingerprintKeyVersion = keyVersion,
                Fingerprint = hmac.ComputeHash(input)
            };
        }
        catch
        {
            throw new CredentialProtectionException("Credential fingerprinting failed closed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(materialBytes);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    internal bool ActiveKeyEquals(VersionedCredentialKeyRing other) =>
        _keyRing.ActiveKeyEquals(other);

    private static byte[] BuildFingerprintInput(
        CredentialProtectionContext context,
        ReadOnlySpan<byte> material)
    {
        var domain = Encoding.ASCII.GetBytes("APIHunterV2/CredentialFingerprint/v1");
        var input = new byte[domain.Length + sizeof(int) + 16 + sizeof(int) + material.Length];
        domain.CopyTo(input, 0);
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(domain.Length, sizeof(int)),
            (int)context.ProviderKind);
        context.ProviderInstanceStableId.TryWriteBytes(
            input.AsSpan(domain.Length + sizeof(int), 16));
        BinaryPrimitives.WriteInt32BigEndian(
            input.AsSpan(domain.Length + sizeof(int) + 16, sizeof(int)),
            material.Length);
        material.CopyTo(input.AsSpan(domain.Length + (2 * sizeof(int)) + 16));
        return input;
    }
}

internal sealed class VersionedCredentialKeyRing
{
    private readonly IReadOnlyDictionary<int, byte[]> _keys;
    private readonly string? _configurationFailure;

    private VersionedCredentialKeyRing(
        IReadOnlyDictionary<int, byte[]> keys,
        int activeVersion,
        string? configurationFailure)
    {
        _keys = keys;
        ActiveVersion = activeVersion;
        _configurationFailure = configurationFailure;
    }

    public int ActiveVersion { get; }
    public bool IsConfigured =>
        _configurationFailure is null && ActiveVersion > 0 && _keys.ContainsKey(ActiveVersion);

    public static VersionedCredentialKeyRing FromExplicit(
        IReadOnlyDictionary<int, byte[]> keys,
        int activeVersion,
        int requiredKeyLength,
        string purpose)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (activeVersion <= 0 || !keys.ContainsKey(activeVersion))
        {
            throw new CredentialProtectionException(
                $"The active {purpose} key version is unavailable.");
        }

        var copied = new Dictionary<int, byte[]>();
        foreach (var (version, key) in keys)
        {
            if (version <= 0 || key is null || key.Length != requiredKeyLength)
            {
                throw new CredentialProtectionException(
                    $"A configured {purpose} key is invalid.");
            }
            copied[version] = key.ToArray();
        }

        return new VersionedCredentialKeyRing(copied, activeVersion, null);
    }

    public static VersionedCredentialKeyRing FromConfiguration(
        IConfiguration configuration,
        string purpose,
        int requiredKeyLength)
    {
        var activeText = configuration[$"SearchCredentials:{purpose}:ActiveKeyVersion"] ??
                         configuration[$"SearchCredentials:{purpose}Keys:ActiveVersion"] ??
                         configuration[$"Credential{purpose}:ActiveKeyVersion"];
        var activeVersion = int.TryParse(activeText, out var parsedVersion) && parsedVersion > 0
            ? parsedVersion
            : 0;
        var keys = new Dictionary<int, byte[]>();
        string? failure = activeVersion == 0
            ? $"The active {purpose.ToLowerInvariant()} key version is unavailable."
            : null;

        foreach (var sectionPath in new[]
                 {
                     $"SearchCredentials:{purpose}:Keys",
                     $"SearchCredentials:{purpose}Keys",
                     $"Credential{purpose}:Keys"
                 })
        {
            foreach (var child in configuration.GetSection(sectionPath).GetChildren())
            {
                if (!int.TryParse(child.Key, out var version) || version <= 0 ||
                    string.IsNullOrWhiteSpace(child.Value))
                {
                    failure ??= $"A configured {purpose.ToLowerInvariant()} key is invalid.";
                    continue;
                }

                try
                {
                    var decoded = Convert.FromBase64String(child.Value);
                    if (decoded.Length != requiredKeyLength)
                    {
                        failure ??= $"A configured {purpose.ToLowerInvariant()} key is invalid.";
                        continue;
                    }
                    keys[version] = decoded;
                }
                catch (FormatException)
                {
                    failure ??= $"A configured {purpose.ToLowerInvariant()} key is invalid.";
                }
            }
        }

        if (activeVersion > 0 && !keys.ContainsKey(activeVersion))
        {
            failure ??= $"The active {purpose.ToLowerInvariant()} key version is unavailable.";
        }

        return new VersionedCredentialKeyRing(keys, activeVersion, failure);
    }

    public bool HasVersion(int version) =>
        _configurationFailure is null && version > 0 && _keys.ContainsKey(version);

    public byte[] RequireActiveKey()
    {
        if (!IsConfigured)
        {
            throw new CredentialProtectionException("The active credential key is unavailable.");
        }
        return _keys[ActiveVersion];
    }

    public byte[] RequireKey(int version)
    {
        if (_configurationFailure is not null || !_keys.TryGetValue(version, out var key))
        {
            throw new CredentialProtectionException("The required credential key version is unavailable.");
        }
        return key;
    }

    public bool ActiveKeyEquals(VersionedCredentialKeyRing other)
    {
        if (!IsConfigured || !other.IsConfigured)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            _keys[ActiveVersion],
            other._keys[other.ActiveVersion]);
    }
}
