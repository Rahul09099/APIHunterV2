using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;
using Xunit;
using Xunit.Sdk;

namespace UnsecuredAPIKeys.Tests.Infrastructure;

internal readonly record struct CredentialProtectionTestContext(
    Guid CredentialStableId,
    SearchProviderEnum ProviderKind,
    Guid ProviderInstanceStableId);

internal readonly record struct FingerprintObservation(int KeyVersion, byte[] Bytes);

/// <summary>
/// Reflection seam for Task 4.1's expected-red tests. The seam keeps the test project
/// buildable before Task 4.2 introduces the production protection contracts while still
/// exercising those contracts behaviorally once they exist.
/// </summary>
internal static class CredentialProtectionTestContract
{
    private static readonly string[] ProtectionServiceNames =
    [
        "CredentialProtectionService"
    ];

    private static readonly string[] FingerprintServiceNames =
    [
        "CredentialFingerprintService"
    ];

    private static readonly string[] StateServiceNames =
    [
        "CredentialStateService"
    ];

    private static readonly string[] RedactorNames =
    [
        "ControlPlaneSecretRedactor",
        "CredentialSecretRedactor"
    ];

    private static readonly IReadOnlyList<Assembly> ContractAssemblies =
    [
        typeof(DBContext).Assembly,
        typeof(DatabaseService).Assembly,
        typeof(global::Program).Assembly
    ];

    public static IReadOnlyDictionary<int, byte[]> CreateKeys(params int[] versions) =>
        versions.ToDictionary(
            version => version,
            version => SHA256.HashData(Encoding.UTF8.GetBytes($"task-4.1-key-version-{version}")));

    public static CredentialProtectionTestContext CreateContext(int seed)
    {
        var normalized = Math.Abs((long)seed);
        return new CredentialProtectionTestContext(
            DeterministicGuid(normalized, 1),
            normalized % 2 == 0 ? SearchProviderEnum.GitHub : SearchProviderEnum.GitLab,
            DeterministicGuid(normalized, 2));
    }

    public static string CreateCredentialMaterial(int seed) =>
        $"task-4.1-provider-credential-{Math.Abs((long)seed):x16}-Ω";

    public static object CreateProtectionService(
        IReadOnlyDictionary<int, byte[]> keys,
        int activeVersion) =>
        CreateKeyedService(
            RequireConcreteType(ProtectionServiceNames),
            keys,
            activeVersion,
            "protection");

    public static object CreateFingerprintService(
        IReadOnlyDictionary<int, byte[]> keys,
        int activeVersion) =>
        CreateKeyedService(
            RequireConcreteType(FingerprintServiceNames),
            keys,
            activeVersion,
            "fingerprint");

    public static object CreateStateService()
    {
        var type = RequireConcreteType(StateServiceNames);
        var constructor = type.GetConstructor(Type.EmptyTypes);
        Assert.True(
            constructor is not null,
            $"Expected {type.FullName} to expose a parameterless pure state-transition constructor.");
        return constructor!.Invoke(null);
    }

    public static object Protect(
        object service,
        string material,
        CredentialProtectionTestContext context)
    {
        var method = RequireOperationMethod(
            service.GetType(),
            ["Protect", "ProtectCredential", "CreateEnvelope", "Encrypt", "ProtectAsync", "ProtectCredentialAsync"]);
        var result = InvokeAndUnwrap(
            service,
            method,
            BuildOperationArguments(method, material, context, envelope: null));
        Assert.NotNull(result);
        return result!;
    }

    public static string Unprotect(
        object service,
        object envelope,
        CredentialProtectionTestContext context)
    {
        var method = RequireOperationMethod(
            service.GetType(),
            ["Unprotect", "Decrypt", "DecryptCredential", "UnprotectAsync", "DecryptAsync", "DecryptCredentialAsync"]);
        var result = InvokeAndUnwrap(
            service,
            method,
            BuildOperationArguments(method, material: null, context, envelope));

        return result switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            null => throw new XunitException($"{method.DeclaringType?.Name}.{method.Name} returned null."),
            _ => ReadStringMember(result, "Material", "CredentialMaterial", "Secret", "Token", "Value")
        };
    }

    public static FingerprintObservation ComputeFingerprint(
        object service,
        string material,
        CredentialProtectionTestContext context,
        int expectedKeyVersion)
    {
        var method = RequireOperationMethod(
            service.GetType(),
            ["ComputeFingerprint", "Fingerprint", "CreateFingerprint", "ComputeFingerprintAsync"]);
        var result = InvokeAndUnwrap(
            service,
            method,
            BuildOperationArguments(method, material, context, envelope: null));
        Assert.NotNull(result);

        if (result is byte[] directBytes)
        {
            return new FingerprintObservation(expectedKeyVersion, directBytes);
        }

        if (result is string directText)
        {
            return new FingerprintObservation(expectedKeyVersion, DecodeBinary(directText));
        }

        var keyVersion = ReadIntMember(
            result!,
            "FingerprintKeyVersion",
            "KeyVersion",
            "Version");
        var bytes = ReadBinaryMember(
            result!,
            "Fingerprint",
            "Digest",
            "Bytes",
            "Value");
        return new FingerprintObservation(keyVersion, bytes);
    }

    public static int ReadEnvelopeFormatVersion(object envelope) =>
        ReadIntMember(envelope, "EnvelopeFormatVersion", "FormatVersion", "EnvelopeVersion");

    public static int ReadProtectionKeyVersion(object envelope) =>
        ReadIntMember(envelope, "ProtectionKeyVersion", "KeyVersion");

    public static byte[] ReadNonce(object envelope) =>
        ReadBinaryMember(envelope, "Nonce", "ProtectionNonce");

    public static byte[] ReadCiphertext(object envelope) =>
        ReadBinaryMember(envelope, "Ciphertext", "CipherText", "ProtectedCiphertext", "EncryptedMaterial");

    public static byte[] ReadAuthenticationTag(object envelope) =>
        ReadBinaryMember(envelope, "AuthenticationTag", "Tag", "ProtectionTag");

    public static object TamperBinary(object envelope, params string[] aliases)
    {
        var clone = CloneObject(envelope);
        var member = RequireMember(clone.GetType(), aliases);
        var original = GetMemberValue(member, clone)
            ?? throw new XunitException($"Envelope member {member.Name} was null.");
        var bytes = ConvertBinaryValue(original);
        Assert.NotEmpty(bytes);
        bytes[0] ^= 0x80;
        SetMemberValue(member, clone, ConvertBinaryForMember(bytes, original, GetMemberType(member)));
        return clone;
    }

    public static object TamperInteger(object envelope, int replacement, params string[] aliases)
    {
        var clone = CloneObject(envelope);
        var member = RequireMember(clone.GetType(), aliases);
        SetMemberValue(member, clone, Convert.ChangeType(replacement, Nullable.GetUnderlyingType(GetMemberType(member)) ?? GetMemberType(member)));
        return clone;
    }

    public static void EnableCredential(object stateService, SearchProviderToken credential)
    {
        var method = RequireOperationMethod(
            stateService.GetType(),
            ["Enable", "Reenable", "SetEnabled", "TransitionToEnabled"]);
        var result = InvokeAndUnwrap(
            stateService,
            method,
            BuildStateArguments(method, credential, reason: null, DateTime.UtcNow));
        CopyReturnedCredential(result, credential);
    }

    public static void DisableCredential(
        object stateService,
        SearchProviderToken credential,
        string reason,
        DateTime disabledAtUtc)
    {
        var method = RequireOperationMethod(
            stateService.GetType(),
            ["Disable", "SetDisabled", "TransitionToDisabled", "Quarantine"]);
        var result = InvokeAndUnwrap(
            stateService,
            method,
            BuildStateArguments(method, credential, reason, disabledAtUtc));
        CopyReturnedCredential(result, credential);
    }

    public static string Redact(string input)
    {
        var type = RequireConcreteType(RedactorNames);
        var instance = type.GetConstructor(Type.EmptyTypes)?.Invoke(null);
        Assert.NotNull(instance);
        var method = RequireOperationMethod(type, ["Redact", "Sanitize"]);
        var result = InvokeAndUnwrap(instance, method, [input]);
        return Assert.IsType<string>(result);
    }

    public static IEntityType RequireCredentialEntity(IModel model)
    {
        var entity = model.FindEntityType(typeof(SearchProviderToken));
        Assert.NotNull(entity);
        Assert.Equal("SearchProviderTokens", entity!.GetTableName());
        return entity;
    }

    public static IProperty RequireSemanticProperty(
        IEntityType entity,
        string semanticName,
        bool nullable,
        IReadOnlyCollection<Type> allowedTypes,
        params string[] aliases)
    {
        var property = aliases
            .Select(entity.FindProperty)
            .FirstOrDefault(candidate => candidate is not null);
        Assert.True(
            property is not null,
            $"Expected SearchProviderToken to persist {semanticName} using one of: {string.Join(", ", aliases)}.");
        Assert.Contains(property!.ClrType, allowedTypes);
        Assert.Equal(nullable, property.IsNullable);
        return property;
    }

    public static void PopulateProtectedCredential(
        SearchProviderToken credential,
        object envelope,
        CredentialProtectionTestContext context)
    {
        SetPropertyIfPresent(credential, "StableId", context.CredentialStableId);
        SetPropertyIfPresent(credential, "ProviderInstanceId", 1L);
        SetPropertyIfPresent(credential, "SearchProvider", context.ProviderKind);
        SetPropertyIfPresent(credential, "IsEnabled", true);
        SetPropertyIfPresent(credential, "DisabledReason", null);
        SetPropertyIfPresent(credential, "DisabledAtUtc", null);

        CopyEnvelopeMember(credential, envelope,
            ["EnvelopeFormatVersion", "FormatVersion", "EnvelopeVersion"],
            ["EnvelopeFormatVersion", "FormatVersion", "EnvelopeVersion"]);
        CopyEnvelopeMember(credential, envelope,
            ["ProtectionKeyVersion", "KeyVersion"],
            ["ProtectionKeyVersion", "KeyVersion"]);
        CopyEnvelopeMember(credential, envelope,
            ["Nonce", "ProtectionNonce"],
            ["ProtectionNonce", "Nonce"]);
        CopyEnvelopeMember(credential, envelope,
            ["Ciphertext", "CipherText", "ProtectedCiphertext", "EncryptedMaterial"],
            ["ProtectedCiphertext", "Ciphertext", "CipherText"]);
        CopyEnvelopeMember(credential, envelope,
            ["AuthenticationTag", "Tag", "ProtectionTag"],
            ["AuthenticationTag", "ProtectionTag", "Tag"]);
    }

    public static object? ReadPublicProperty(object instance, params string[] aliases)
    {
        var property = aliases
            .Select(alias => instance.GetType().GetProperty(alias, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase))
            .FirstOrDefault(candidate => candidate is not null);
        Assert.True(
            property is not null,
            $"Expected {instance.GetType().Name} to expose one of: {string.Join(", ", aliases)}.");
        return property!.GetValue(instance);
    }

    public static void SetPublicProperty(object instance, object? value, params string[] aliases)
    {
        var property = aliases
            .Select(alias => instance.GetType().GetProperty(alias, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase))
            .FirstOrDefault(candidate => candidate is not null);
        Assert.True(
            property is not null,
            $"Expected {instance.GetType().Name} to expose one of: {string.Join(", ", aliases)}.");
        property!.SetValue(instance, ConvertForType(value, property.PropertyType));
    }

    public static void AssertNoPlaintextRetained(object service, string plaintext)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Assert.False(
            ObjectGraphContains(service, plaintext, visited, depth: 0),
            $"{service.GetType().Name} retained plaintext credential material after the operation.");

        foreach (var field in service.GetType().GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.IsLiteral)
            {
                continue;
            }

            object? value;
            try
            {
                value = field.GetValue(null);
            }
            catch
            {
                continue;
            }

            Assert.False(
                ObjectGraphContains(value, plaintext, visited, depth: 0),
                $"Static field {service.GetType().Name}.{field.Name} retained plaintext credential material.");
        }
    }

    public static void AssertDecryptApiIsSingleCredentialScoped()
    {
        var serviceType = RequireConcreteType(ProtectionServiceNames);
        var decryptMethods = serviceType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method =>
                method.Name.Contains("Decrypt", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Unprotect", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(decryptMethods);

        foreach (var method in decryptMethods)
        {
            Assert.DoesNotContain(
                method.GetParameters(),
                parameter => IsBulkCredentialShape(parameter.ParameterType));
            Assert.False(
                IsBulkCredentialShape(UnwrapTaskLikeType(method.ReturnType)),
                $"{serviceType.Name}.{method.Name} exposes a bulk-decryption return shape.");
        }
    }

    public static IReadOnlyList<string> FindNonClaimDtoSecretMembers()
    {
        var forbiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Token",
            "Tokens",
            "NodeToken",
            "ProviderToken",
            "Credential",
            "Credentials",
            "CredentialMaterial",
            "Secret",
            "Fingerprint",
            "FullFingerprint",
            "Authorization"
        };

        return FindTypes()
            .Where(type => type.IsClass || (type.IsValueType && !type.IsEnum))
            .Where(type =>
                type.Name.EndsWith("DTO", StringComparison.OrdinalIgnoreCase) ||
                type.Name.EndsWith("Response", StringComparison.OrdinalIgnoreCase) ||
                type.Name.EndsWith("Projection", StringComparison.OrdinalIgnoreCase))
            .Where(type => !type.Name.Contains("Claim", StringComparison.OrdinalIgnoreCase))
            .SelectMany(type => type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => forbiddenNames.Contains(property.Name))
                .Select(property => $"{type.FullName}.{property.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<string> FindControlPlaneSecretQueryStringViolations()
    {
        var patterns = new[]
        {
            new Regex(
                @"\[FromQuery(?:\([^\]]*\))?\]\s*string\??\s+(?:nodeToken|providerToken|credential|secret|token)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Regex(
                @"(?:\?|&)(?:access_token|private_token|node_token|nodeToken|providerToken|credential|token)\s*=",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Regex(
                @"AddQueryString\s*\([^;]{0,500}(?:nodeToken|providerToken|credential|secret|token)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)
        };

        var violations = new List<string>();
        foreach (var file in EnumerateProductionSourceFiles())
        {
            var source = File.ReadAllText(file);
            foreach (var pattern in patterns)
            {
                var match = pattern.Match(source);
                if (match.Success)
                {
                    violations.Add($"{Path.GetRelativePath(FindRepositoryRoot(), file)}: {SingleLine(match.Value)}");
                }
            }
        }

        return violations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> ReadProtectionPrimitiveSources()
    {
        var root = FindRepositoryRoot();
        return Directory
            .EnumerateFiles(Path.Combine(root, "UnsecuredAPIKeys.Services"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildArtifact(path))
            .Select(File.ReadAllText)
            .ToArray();
    }

    public static Type RequireConcreteTypeForContract(string exactName) =>
        RequireConcreteType([exactName]);

    public static IReadOnlyList<Type> FindOutcomeEnums() =>
        FindTypes()
            .Where(type => type.IsEnum && type.Name.Contains("Outcome", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "UnsecuredAPIKeys-OpenSource.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static Guid DeterministicGuid(long seed, byte discriminator)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{discriminator}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static Type RequireConcreteType(IReadOnlyCollection<string> exactNames)
    {
        var matches = FindTypes()
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => exactNames.Contains(type.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(
            matches.Length > 0,
            $"Expected production type {string.Join(" or ", exactNames)}. Task 4.2 has not implemented this contract yet.");
        Assert.True(
            matches.Length == 1,
            $"Expected one production type for {string.Join(" or ", exactNames)}, found: {string.Join(", ", matches.Select(type => type.FullName))}.");
        return matches[0];
    }

    private static IEnumerable<Type> FindTypes()
    {
        foreach (var assembly in ContractAssemblies.Distinct())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException error)
            {
                types = error.Types.Where(type => type is not null).Cast<Type>().ToArray();
            }

            foreach (var type in types)
            {
                yield return type;
            }
        }
    }

    private static object CreateKeyedService(
        Type serviceType,
        IReadOnlyDictionary<int, byte[]> keys,
        int activeVersion,
        string purpose)
    {
        foreach (var constructor in serviceType
                     .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                     .OrderByDescending(candidate => candidate.GetParameters().Length))
        {
            if (!TryBuildKeyedConstructorArguments(
                    constructor,
                    keys,
                    activeVersion,
                    purpose,
                    out var arguments))
            {
                continue;
            }

            try
            {
                return constructor.Invoke(arguments);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            }
        }

        throw new XunitException(
            $"Expected {serviceType.FullName} to accept versioned {purpose} keys and an active key version through its public constructor/options contract.");
    }

    private static bool TryBuildKeyedConstructorArguments(
        ConstructorInfo constructor,
        IReadOnlyDictionary<int, byte[]> keys,
        int activeVersion,
        string purpose,
        out object?[] arguments)
    {
        var parameters = constructor.GetParameters();
        arguments = new object?[parameters.Length];
        var suppliedKeyMaterial = false;
        var suppliedActiveVersion = false;

        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var parameterType = parameter.ParameterType;

            if (parameterType == typeof(int) &&
                (parameter.Name?.Contains("version", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                arguments[index] = activeVersion;
                suppliedActiveVersion = true;
                continue;
            }

            if (TryConvertKeyDictionary(parameterType, keys, out var dictionary))
            {
                arguments[index] = dictionary;
                suppliedKeyMaterial = true;
                continue;
            }

            if (parameterType == typeof(byte[]) &&
                (parameter.Name?.Contains("key", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                arguments[index] = keys[activeVersion].ToArray();
                suppliedKeyMaterial = true;
                continue;
            }

            if (parameterType == typeof(IConfiguration))
            {
                arguments[index] = CreateKeyConfiguration(keys, activeVersion, purpose);
                suppliedKeyMaterial = true;
                suppliedActiveVersion = true;
                continue;
            }

            if (parameterType.IsGenericType &&
                parameterType.GetGenericTypeDefinition() == typeof(IOptions<>))
            {
                var optionsType = parameterType.GetGenericArguments()[0];
                if (!TryCreateKeyContainer(optionsType, keys, activeVersion, out var optionsValue))
                {
                    return false;
                }

                var wrapperType = typeof(OptionsWrapper<>).MakeGenericType(optionsType);
                arguments[index] = Activator.CreateInstance(wrapperType, optionsValue);
                suppliedKeyMaterial = true;
                suppliedActiveVersion = true;
                continue;
            }

            if (parameterType.Name.Contains("KeyRing", StringComparison.OrdinalIgnoreCase) ||
                parameterType.Name.Contains("Options", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryCreateKeyContainer(parameterType, keys, activeVersion, out var keyContainer))
                {
                    return false;
                }

                arguments[index] = keyContainer;
                suppliedKeyMaterial = true;
                suppliedActiveVersion = true;
                continue;
            }

            if (parameterType.IsGenericType &&
                parameterType.GetGenericTypeDefinition().FullName == "Microsoft.Extensions.Logging.ILogger`1")
            {
                var loggerType = typeof(NullLogger<>).MakeGenericType(parameterType.GetGenericArguments()[0]);
                arguments[index] = loggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                continue;
            }

            if (parameter.HasDefaultValue)
            {
                arguments[index] = parameter.DefaultValue;
                continue;
            }

            return false;
        }

        if (parameters.Length == 0)
        {
            return true;
        }

        return suppliedKeyMaterial && suppliedActiveVersion;
    }

    private static bool TryCreateKeyContainer(
        Type containerType,
        IReadOnlyDictionary<int, byte[]> keys,
        int activeVersion,
        out object? container)
    {
        container = null;
        var constructor = containerType.GetConstructor(Type.EmptyTypes);
        if (constructor is null)
        {
            return false;
        }

        container = constructor.Invoke(null);
        var keysSet = false;
        var versionSet = false;
        foreach (var property in containerType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanWrite)
            {
                continue;
            }

            if (property.Name.Contains("Active", StringComparison.OrdinalIgnoreCase) &&
                property.Name.Contains("Version", StringComparison.OrdinalIgnoreCase) &&
                property.PropertyType == typeof(int))
            {
                property.SetValue(container, activeVersion);
                versionSet = true;
                continue;
            }

            if (property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase) &&
                TryConvertKeyDictionary(property.PropertyType, keys, out var dictionary))
            {
                property.SetValue(container, dictionary);
                keysSet = true;
            }
        }

        return keysSet && versionSet;
    }

    private static bool TryConvertKeyDictionary(
        Type targetType,
        IReadOnlyDictionary<int, byte[]> keys,
        out object? converted)
    {
        var byteKeys = keys.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        if (targetType.IsAssignableFrom(byteKeys.GetType()))
        {
            converted = byteKeys;
            return true;
        }

        var stringKeys = keys.ToDictionary(pair => pair.Key, pair => Convert.ToBase64String(pair.Value));
        if (targetType.IsAssignableFrom(stringKeys.GetType()))
        {
            converted = stringKeys;
            return true;
        }

        converted = null;
        return false;
    }

    private static IConfiguration CreateKeyConfiguration(
        IReadOnlyDictionary<int, byte[]> keys,
        int activeVersion,
        string purpose)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"SearchCredentials:{purpose}:ActiveKeyVersion"] = activeVersion.ToString(),
            [$"SearchCredentials:{purpose}Keys:ActiveVersion"] = activeVersion.ToString(),
            [$"Credential{purpose}:ActiveKeyVersion"] = activeVersion.ToString()
        };
        foreach (var pair in keys)
        {
            var encoded = Convert.ToBase64String(pair.Value);
            values[$"SearchCredentials:{purpose}:Keys:{pair.Key}"] = encoded;
            values[$"SearchCredentials:{purpose}Keys:{pair.Key}"] = encoded;
            values[$"Credential{purpose}:Keys:{pair.Key}"] = encoded;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static MethodInfo RequireOperationMethod(Type type, IReadOnlyCollection<string> names)
    {
        var methods = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => names.Contains(method.Name, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(method => method.GetParameters().Length)
            .ToArray();
        Assert.True(
            methods.Length > 0,
            $"Expected {type.FullName} to expose one of: {string.Join(", ", names)}.");
        return methods[0];
    }

    private static object?[] BuildOperationArguments(
        MethodInfo method,
        string? material,
        CredentialProtectionTestContext context,
        object? envelope)
    {
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var type = parameter.ParameterType;
            var name = parameter.Name ?? string.Empty;

            if (envelope is not null && type.IsInstanceOfType(envelope))
            {
                arguments[index] = envelope;
                continue;
            }

            if (envelope is not null && name.Contains("envelope", StringComparison.OrdinalIgnoreCase))
            {
                arguments[index] = envelope;
                continue;
            }

            if (type == typeof(string))
            {
                if (name.Contains("instance", StringComparison.OrdinalIgnoreCase))
                {
                    arguments[index] = context.ProviderInstanceStableId.ToString("D");
                }
                else if (name.Contains("stable", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("credentialId", StringComparison.OrdinalIgnoreCase))
                {
                    arguments[index] = context.CredentialStableId.ToString("D");
                }
                else if (name.Contains("kind", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("provider", StringComparison.OrdinalIgnoreCase))
                {
                    arguments[index] = context.ProviderKind.ToString();
                }
                else
                {
                    arguments[index] = material;
                }
                continue;
            }

            if (type == typeof(byte[]) && material is not null)
            {
                arguments[index] = Encoding.UTF8.GetBytes(material);
                continue;
            }

            if (type == typeof(Guid))
            {
                arguments[index] = name.Contains("instance", StringComparison.OrdinalIgnoreCase)
                    ? context.ProviderInstanceStableId
                    : context.CredentialStableId;
                continue;
            }

            if (type.IsEnum &&
                (type == typeof(SearchProviderEnum) ||
                 name.Contains("kind", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("provider", StringComparison.OrdinalIgnoreCase)))
            {
                arguments[index] = Enum.Parse(type, context.ProviderKind.ToString(), ignoreCase: true);
                continue;
            }

            if (type == typeof(CancellationToken))
            {
                arguments[index] = CancellationToken.None;
                continue;
            }

            if (TryCreateContext(type, context, out var productionContext))
            {
                arguments[index] = productionContext;
                continue;
            }

            throw new XunitException(
                $"Cannot supply {method.DeclaringType?.Name}.{method.Name} parameter '{name}' ({type.FullName}) from the credential protection contract.");
        }

        return arguments;
    }

    private static bool TryCreateContext(
        Type contextType,
        CredentialProtectionTestContext context,
        out object? value)
    {
        value = null;
        if (!contextType.Name.Contains("Context", StringComparison.OrdinalIgnoreCase) &&
            !contextType.Name.Contains("Identity", StringComparison.OrdinalIgnoreCase) &&
            !contextType.Name.Contains("AssociatedData", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parameterless = contextType.GetConstructor(Type.EmptyTypes);
        if (parameterless is not null)
        {
            value = parameterless.Invoke(null);
            SetPropertyIfPresent(value, "CredentialStableId", context.CredentialStableId);
            SetPropertyIfPresent(value, "StableId", context.CredentialStableId);
            SetPropertyIfPresent(value, "ProviderKind", context.ProviderKind);
            SetPropertyIfPresent(value, "SearchProvider", context.ProviderKind);
            SetPropertyIfPresent(value, "ProviderInstanceStableId", context.ProviderInstanceStableId);
            SetPropertyIfPresent(value, "InstanceStableId", context.ProviderInstanceStableId);
            return true;
        }

        foreach (var constructor in contextType.GetConstructors())
        {
            try
            {
                var arguments = constructor.GetParameters()
                    .Select(parameter =>
                    {
                        if (parameter.ParameterType == typeof(Guid))
                        {
                            return (object)(parameter.Name?.Contains("instance", StringComparison.OrdinalIgnoreCase) == true
                                ? context.ProviderInstanceStableId
                                : context.CredentialStableId);
                        }

                        if (parameter.ParameterType.IsEnum)
                        {
                            return Enum.Parse(parameter.ParameterType, context.ProviderKind.ToString(), true);
                        }

                        throw new InvalidOperationException();
                    })
                    .ToArray();
                value = constructor.Invoke(arguments);
                return true;
            }
            catch
            {
                // Try the next constructor shape.
            }
        }

        return false;
    }

    private static object?[] BuildStateArguments(
        MethodInfo method,
        SearchProviderToken credential,
        string? reason,
        DateTime disabledAtUtc)
    {
        var arguments = new object?[method.GetParameters().Length];
        for (var index = 0; index < method.GetParameters().Length; index++)
        {
            var parameter = method.GetParameters()[index];
            var type = parameter.ParameterType;
            if (type.IsInstanceOfType(credential))
            {
                arguments[index] = credential;
            }
            else if (type == typeof(string))
            {
                arguments[index] = reason;
            }
            else if (type == typeof(DateTime))
            {
                arguments[index] = disabledAtUtc;
            }
            else if (type == typeof(DateTimeOffset))
            {
                arguments[index] = new DateTimeOffset(disabledAtUtc);
            }
            else if (type.IsEnum && reason is not null)
            {
                arguments[index] = Enum.Parse(type, reason, ignoreCase: true);
            }
            else if (type == typeof(CancellationToken))
            {
                arguments[index] = CancellationToken.None;
            }
            else if (parameter.HasDefaultValue)
            {
                arguments[index] = parameter.DefaultValue;
            }
            else
            {
                throw new XunitException(
                    $"Cannot supply state-transition parameter {parameter.Name} ({type.FullName}).");
            }
        }

        return arguments;
    }

    private static object? InvokeAndUnwrap(object? target, MethodInfo method, object?[] arguments)
    {
        object? invocation;
        try
        {
            invocation = method.Invoke(target, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }

        if (invocation is not Task task)
        {
            var invocationType = invocation?.GetType();
            var asTask = invocationType?.GetMethod("AsTask", BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);
            if (asTask is not null && asTask.Invoke(invocation, null) is Task convertedTask)
            {
                task = convertedTask;
            }
            else
            {
                return invocation;
            }
        }

        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            ExceptionDispatchInfo.Capture(UnwrapException(error)).Throw();
        }

        return task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(task);
    }

    private static Exception UnwrapException(Exception error)
    {
        while (error is TargetInvocationException or AggregateException && error.InnerException is not null)
        {
            error = error.InnerException;
        }
        return error;
    }

    private static int ReadIntMember(object instance, params string[] aliases)
    {
        var value = ReadMemberValue(instance, aliases);
        return Convert.ToInt32(value);
    }

    private static byte[] ReadBinaryMember(object instance, params string[] aliases) =>
        ConvertBinaryValue(ReadMemberValue(instance, aliases));

    private static string ReadStringMember(object instance, params string[] aliases)
    {
        var value = ReadMemberValue(instance, aliases);
        return value switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => throw new XunitException(
                $"Expected {instance.GetType().Name} member {string.Join("/", aliases)} to contain credential material as string or bytes.")
        };
    }

    private static object ReadMemberValue(object instance, params string[] aliases)
    {
        var member = RequireMember(instance.GetType(), aliases);
        return GetMemberValue(member, instance)
            ?? throw new XunitException($"{instance.GetType().Name}.{member.Name} was null.");
    }

    private static MemberInfo RequireMember(Type type, params string[] aliases)
    {
        var members = type
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(member => member is PropertyInfo or FieldInfo)
            .ToArray();
        var member = aliases
            .Select(alias => members.FirstOrDefault(candidate => candidate.Name.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(candidate => candidate is not null);
        Assert.True(
            member is not null,
            $"Expected {type.FullName} to expose envelope member {string.Join(" or ", aliases)}.");
        return member!;
    }

    private static object? GetMemberValue(MemberInfo member, object instance) => member switch
    {
        PropertyInfo property => property.GetValue(instance),
        FieldInfo field => field.GetValue(instance),
        _ => throw new NotSupportedException()
    };

    private static Type GetMemberType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => throw new NotSupportedException()
    };

    private static void SetMemberValue(MemberInfo member, object instance, object? value)
    {
        switch (member)
        {
            case PropertyInfo property when property.SetMethod is not null:
                property.SetValue(instance, value);
                return;
            case FieldInfo field when !field.IsInitOnly:
                field.SetValue(instance, value);
                return;
            case PropertyInfo property:
                var backingField = instance.GetType().GetField(
                    $"<{property.Name}>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(backingField);
                backingField!.SetValue(instance, value);
                return;
            default:
                throw new XunitException($"Envelope member {member.Name} cannot be changed for tamper testing.");
        }
    }

    private static object CloneObject(object instance)
    {
        var cloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(cloneMethod);
        return cloneMethod!.Invoke(instance, null)
            ?? throw new XunitException($"Could not clone {instance.GetType().FullName} for tamper testing.");
    }

    private static byte[] ConvertBinaryValue(object value) => value switch
    {
        byte[] bytes => bytes.ToArray(),
        Memory<byte> memory => memory.ToArray(),
        ReadOnlyMemory<byte> memory => memory.ToArray(),
        string text => DecodeBinary(text),
        _ => throw new XunitException($"Unsupported binary contract type {value.GetType().FullName}.")
    };

    private static byte[] DecodeBinary(string text)
    {
        if (text.Length > 0 && text.Length % 2 == 0 && text.All(Uri.IsHexDigit))
        {
            return Convert.FromHexString(text);
        }

        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return Encoding.UTF8.GetBytes(text);
        }
    }

    private static object ConvertBinaryForMember(byte[] bytes, object original, Type memberType)
    {
        if (memberType == typeof(byte[]))
        {
            return bytes;
        }
        if (memberType == typeof(Memory<byte>))
        {
            return new Memory<byte>(bytes);
        }
        if (memberType == typeof(ReadOnlyMemory<byte>))
        {
            return new ReadOnlyMemory<byte>(bytes);
        }
        if (memberType == typeof(string))
        {
            var originalText = (string)original;
            return originalText.Length > 0 && originalText.Length % 2 == 0 && originalText.All(Uri.IsHexDigit)
                ? Convert.ToHexString(bytes).ToLowerInvariant()
                : Convert.ToBase64String(bytes);
        }

        throw new XunitException($"Unsupported binary envelope member type {memberType.FullName}.");
    }

    private static void CopyEnvelopeMember(
        object target,
        object envelope,
        IReadOnlyList<string> sourceAliases,
        IReadOnlyList<string> targetAliases)
    {
        var sourceMember = sourceAliases
            .Select(alias => envelope.GetType().GetProperty(alias, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase))
            .FirstOrDefault(candidate => candidate is not null);
        if (sourceMember is null)
        {
            return;
        }

        var targetProperty = targetAliases
            .Select(alias => target.GetType().GetProperty(alias, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase))
            .FirstOrDefault(candidate => candidate is not null);
        if (targetProperty is null)
        {
            return;
        }

        targetProperty.SetValue(
            target,
            ConvertForType(sourceMember.GetValue(envelope), targetProperty.PropertyType));
    }

    private static object? ConvertForType(object? value, Type destinationType)
    {
        if (value is null)
        {
            return null;
        }

        var target = Nullable.GetUnderlyingType(destinationType) ?? destinationType;
        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        if (value is string text && target == typeof(byte[]))
        {
            return DecodeBinary(text);
        }
        if (value is byte[] bytes && target == typeof(string))
        {
            return Convert.ToBase64String(bytes);
        }
        if (target.IsEnum)
        {
            return value is string enumName
                ? Enum.Parse(target, enumName, true)
                : Enum.ToObject(target, Convert.ToInt32(value));
        }

        return Convert.ChangeType(value, target);
    }

    private static void SetPropertyIfPresent(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property?.SetMethod is not null)
        {
            property.SetValue(target, ConvertForType(value, property.PropertyType));
        }
    }

    private static void CopyReturnedCredential(object? result, SearchProviderToken target)
    {
        if (result is not SearchProviderToken returned || ReferenceEquals(returned, target))
        {
            return;
        }

        foreach (var name in new[] { "IsEnabled", "DisabledReason", "DisabledAtUtc" })
        {
            var source = returned.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            var destination = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (source is not null && destination?.SetMethod is not null)
            {
                destination.SetValue(target, source.GetValue(returned));
            }
        }
    }

    private static bool ObjectGraphContains(
        object? value,
        string plaintext,
        ISet<object> visited,
        int depth)
    {
        if (value is null || depth > 4)
        {
            return false;
        }
        if (value is string text)
        {
            return text.Contains(plaintext, StringComparison.Ordinal);
        }
        if (value is byte[] bytes)
        {
            return Encoding.UTF8.GetString(bytes).Contains(plaintext, StringComparison.Ordinal);
        }
        if (value.GetType().IsPrimitive || value is decimal or DateTime or DateTimeOffset or Guid or Enum)
        {
            return false;
        }
        if (!value.GetType().IsValueType && !visited.Add(value))
        {
            return false;
        }

        if (value is IDictionary dictionary)
        {
            var inspected = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (ObjectGraphContains(entry.Key, plaintext, visited, depth + 1) ||
                    ObjectGraphContains(entry.Value, plaintext, visited, depth + 1))
                {
                    return true;
                }
                if (++inspected >= 100)
                {
                    break;
                }
            }
            return false;
        }

        if (value is IEnumerable enumerable)
        {
            var inspected = 0;
            foreach (var item in enumerable)
            {
                if (ObjectGraphContains(item, plaintext, visited, depth + 1))
                {
                    return true;
                }
                if (++inspected >= 100)
                {
                    break;
                }
            }
            return false;
        }

        foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? fieldValue;
            try
            {
                fieldValue = field.GetValue(value);
            }
            catch
            {
                continue;
            }

            if (ObjectGraphContains(fieldValue, plaintext, visited, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBulkCredentialShape(Type type)
    {
        if (type == typeof(string) || type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>))
        {
            return false;
        }

        if (type.IsArray)
        {
            return true;
        }

        return type.GetInterfaces()
            .Concat([type])
            .Any(candidate => candidate.IsGenericType &&
                              (candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>) ||
                               candidate.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>) ||
                               candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                               candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
    }

    private static Type UnwrapTaskLikeType(Type type)
    {
        if (type.IsGenericType &&
            (type.GetGenericTypeDefinition() == typeof(Task<>) ||
             type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            return type.GetGenericArguments()[0];
        }
        return type;
    }

    private static IEnumerable<string> EnumerateProductionSourceFiles()
    {
        var root = FindRepositoryRoot();
        foreach (var directory in new[]
                 {
                     "UnsecuredAPIKeys.Data",
                     "UnsecuredAPIKeys.Services",
                     "UnsecuredAPIKeys.Providers",
                     "UnsecuredAPIKeys.WebAPI"
                 })
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                if (!IsBuildArtifact(file))
                {
                    yield return file;
                }
            }
        }
    }

    private static bool IsBuildArtifact(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string SingleLine(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();
}
