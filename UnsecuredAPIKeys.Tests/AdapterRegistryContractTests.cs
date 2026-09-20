using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Services;
using Xunit;
using Xunit.Sdk;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Expected-red contract tests for multi-search-provider-platform Task 6.1.
/// Runtime reflection keeps this test increment compile-clean until Task 6.2 introduces
/// the adapter registry and capability metadata contracts.
/// </summary>
public sealed class AdapterRegistryContractTests
{
    /// <summary>
    /// **Validates: Requirements 8.1, 8.4-8.12**
    /// </summary>
    [Fact]
    public void AdapterContract_ExposesVersionedMetadataAndAllBoundedOperations()
    {
        var adapterType = AdapterContractProbe.RequireType("ISearchProviderAdapter");

        Assert.True(adapterType.IsInterface, "ISearchProviderAdapter must be an interface resolved through DI.");
        AdapterContractProbe.RequireProperty(adapterType, "ProviderKind", property => property.PropertyType.IsEnum);
        AdapterContractProbe.RequireProperty(adapterType, "AdapterVersion", property => property.PropertyType == typeof(string));
        AdapterContractProbe.RequireProperty(adapterType, "DeclaredCapabilities", property => property.PropertyType.IsEnum);
        AdapterContractProbe.RequireProperty(adapterType, "SettingsSchema", property =>
            property.PropertyType.Name.Contains("SettingsSchema", StringComparison.Ordinal));

        AdapterContractProbe.RequireMethod(adapterType, "DiscoverCapabilitiesAsync");
        AdapterContractProbe.RequireMethod(adapterType, "ValidateCredentialAsync");
        AdapterContractProbe.RequireMethod(adapterType, "TranslateQueryAsync");
        AdapterContractProbe.RequireMethod(adapterType, "SearchAsync");
        AdapterContractProbe.RequireMethod(adapterType, "FetchContentAsync");
    }

    /// <summary>
    /// **Validates: Requirements 2.15, 8.5**
    /// </summary>
    [Fact]
    public void CapabilityModel_RepresentsEveryCapabilityAsAnIndependentFlag()
    {
        var capabilityType = AdapterContractProbe.RequireType("SearchProviderCapability");
        Assert.True(capabilityType.IsEnum, "SearchProviderCapability must be a closed flags enum.");
        Assert.NotNull(capabilityType.GetCustomAttribute<FlagsAttribute>());

        var required = new[]
        {
            "CodeSearch",
            "PaginatedSearch",
            "StreamingSearch",
            "ContentRetrieval",
            "PrivateRepositories",
            "GlobalPublicSearch",
            "SelfHosted",
            "RepositoryMetadata",
            "NativeQueryOverrides"
        };

        var names = Enum.GetNames(capabilityType);
        Assert.All(required, name => Assert.Contains(name, names));

        var values = required
            .Select(name => Convert.ToUInt64(Enum.Parse(capabilityType, name)))
            .ToArray();
        Assert.All(values, value => Assert.True(
            value != 0 && (value & (value - 1)) == 0,
            $"Capability value {value} must be one independent bit."));
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    /// <summary>
    /// **Validates: Requirements 8.1-8.2**
    /// </summary>
    [Fact]
    public void Registry_RejectsTwoDiRegistrationsForTheSameProviderKind()
    {
        var adapterType = AdapterContractProbe.RequireType("ISearchProviderAdapter");
        var first = AdapterContractProbe.CreateFakeAdapter(adapterType, SearchProviderEnum.GitHub, "fake-1.0");
        var second = AdapterContractProbe.CreateFakeAdapter(adapterType, SearchProviderEnum.GitHub, "fake-2.0");

        var error = Record.Exception(() =>
        {
            var registry = AdapterContractProbe.CreateRegistry(adapterType, first.Adapter, second.Adapter);
            AdapterContractProbe.ResolveAdapter(registry, SearchProviderEnum.GitHub);
        });

        Assert.NotNull(error);
        Assert.Contains("GitHub", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(
            error is InvalidOperationException || error.InnerException is InvalidOperationException,
            $"Duplicate registrations must fail deterministically, but got {error.GetType().Name}.");
    }

    /// <summary>
    /// **Validates: Requirements 8.3**
    /// </summary>
    [Fact]
    public void Registry_UnknownKindFailsClosedWithoutReturningTheGitHubFake()
    {
        var adapterType = AdapterContractProbe.RequireType("ISearchProviderAdapter");
        var github = AdapterContractProbe.CreateFakeAdapter(adapterType, SearchProviderEnum.GitHub, "fake-1.0");
        var registry = AdapterContractProbe.CreateRegistry(adapterType, github.Adapter);

        var error = Record.Exception(() =>
            AdapterContractProbe.ResolveAdapter(registry, SearchProviderEnum.Unknown));

        Assert.NotNull(error);
        Assert.Equal(0, github.Probe.OperationInvocations);
        Assert.DoesNotContain("fallback", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **Validates: Requirements 2.16-2.18**
    /// </summary>
    [Fact]
    public void CapabilityEvaluation_IntersectsDeclaredAndDiscoveredAndInvalidatesDiscoveryFailure()
    {
        var capabilityType = AdapterContractProbe.RequireType("SearchProviderCapability");
        var codeSearch = AdapterContractProbe.ParseCapability(capabilityType, "CodeSearch");
        var content = AdapterContractProbe.ParseCapability(capabilityType, "ContentRetrieval");
        var streaming = AdapterContractProbe.ParseCapability(capabilityType, "StreamingSearch");
        var declared = AdapterContractProbe.CombineFlags(capabilityType, codeSearch, content);
        var discovered = AdapterContractProbe.CombineFlags(capabilityType, codeSearch, streaming);

        var effective = AdapterContractProbe.EvaluateCapabilities(declared, discovered, discoverySucceeded: true);
        Assert.True(AdapterContractProbe.HasFlag(effective, codeSearch));
        Assert.False(AdapterContractProbe.HasFlag(effective, content));
        Assert.False(AdapterContractProbe.HasFlag(effective, streaming));

        var invalidated = AdapterContractProbe.EvaluateCapabilities(declared, discovered, discoverySucceeded: false);
        Assert.Equal(0UL, Convert.ToUInt64(invalidated));
    }

    /// <summary>
    /// **Validates: Requirements 8.12, 8.20**
    /// </summary>
    [Fact]
    public void SearchAndContentOperations_ShareOneTypedOutcomeClassificationModel()
    {
        var adapterType = AdapterContractProbe.RequireType("ISearchProviderAdapter");
        var outcomeType = AdapterContractProbe.RequireType("ProviderOutcomeKind");
        var expectedOutcomes = new[]
        {
            "Success",
            "RateLimited",
            "AuthInvalid",
            "ForbiddenScope",
            "Transient",
            "RequestInvalid",
            "ResourceMissing",
            "Cancellation"
        };
        Assert.All(expectedOutcomes, name => Assert.Contains(name, Enum.GetNames(outcomeType)));

        var search = AdapterContractProbe.RequireMethod(adapterType, "SearchAsync");
        var content = AdapterContractProbe.RequireMethod(adapterType, "FetchContentAsync");
        Assert.True(
            AdapterContractProbe.TypeGraphContains(search.ReturnType, outcomeType),
            "Search results must carry the shared ProviderOutcomeKind classification.");
        Assert.True(
            AdapterContractProbe.TypeGraphContains(content.ReturnType, outcomeType),
            "Content results must carry the same ProviderOutcomeKind classification.");
    }

    /// <summary>
    /// **Validates: Requirements 8.21-8.23**
    /// </summary>
    [Fact]
    public void OperationContext_RequiresBoundsForUntrustedProviderAndRepositoryContent()
    {
        var contextType = AdapterContractProbe.RequireType("ProviderOperationContext");
        var searchableContract = string.Join(
            '|',
            AdapterContractProbe.FlattenPublicContract(contextType, depth: 2));

        foreach (var requiredBound in new[]
                 {
                     "ResponseSize",
                     "ContentType",
                     "EventCount"
                 })
        {
            Assert.Contains(requiredBound, searchableContract, StringComparison.OrdinalIgnoreCase);
        }

        var adapterType = AdapterContractProbe.RequireType("ISearchProviderAdapter");
        var unsafeOperations = adapterType.GetMethods()
            .Select(method => method.Name)
            .Where(name =>
                name.Contains("ExecuteRepository", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("InterpretRepository", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("RunContent", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(unsafeOperations);
    }

    /// <summary>
    /// **Validates: Requirements 8.25-8.26**
    /// </summary>
    [Fact]
    public void KindGuard_RejectsInstanceAdapterMismatchBeforeFakeProviderInvocation()
    {
        var adapterType = AdapterContractProbe.RequireType("ISearchProviderAdapter");
        var github = AdapterContractProbe.CreateFakeAdapter(adapterType, SearchProviderEnum.GitHub, "fake-1.0");
        var gitlabInstance = new SearchProviderInstance
        {
            StableId = Guid.NewGuid(),
            ProviderKind = SearchProviderEnum.GitLab,
            DisplayName = "Mismatched fake instance",
            NormalizedScheme = "https",
            NormalizedHost = "provider.invalid",
            NormalizedPort = 443,
            NormalizedBasePath = "/api"
        };

        var error = Record.Exception(() =>
            AdapterContractProbe.ValidateAdapterKind(github.Adapter, gitlabInstance));

        Assert.NotNull(error);
        Assert.Equal(0, github.Probe.OperationInvocations);
        Assert.Contains("kind", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **Validates: Requirements 1.1-1.3, 2.17, 8.21-8.23**
    /// </summary>
    [Fact]
    public void UnsupportedOfficialCapability_IsRejectedWithoutScrapingOrPrivateEndpointSubstitution()
    {
        var capabilityType = AdapterContractProbe.RequireType("SearchProviderCapability");
        var codeSearch = AdapterContractProbe.ParseCapability(capabilityType, "CodeSearch");
        var content = AdapterContractProbe.ParseCapability(capabilityType, "ContentRetrieval");
        var declared = AdapterContractProbe.CombineFlags(capabilityType, content);
        var discovered = AdapterContractProbe.CombineFlags(capabilityType, codeSearch, content);

        var effective = AdapterContractProbe.EvaluateCapabilities(declared, discovered, discoverySucceeded: true);
        Assert.False(AdapterContractProbe.HasFlag(effective, codeSearch));

        var adapterType = AdapterContractProbe.RequireType("ISearchProviderAdapter");
        var forbiddenFallbacks = adapterType.GetMethods()
            .Select(method => method.Name)
            .Where(name =>
                name.Contains("Scrape", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("PrivateEndpoint", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Undocumented", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Bypass", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(forbiddenFallbacks);
    }
}

internal static class AdapterContractProbe
{
    private static readonly Assembly[] ProductionAssemblies =
    [
        typeof(SearchProviderInstance).Assembly,
        typeof(ISearchProvider).Assembly,
        typeof(ScraperService).Assembly
    ];

    public static Type RequireType(string simpleName)
    {
        var matches = ProductionAssemblies
            .SelectMany(GetLoadableTypes)
            .Where(type => type.Name.Equals(simpleName, StringComparison.Ordinal))
            .Distinct()
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new XunitException(
                $"Missing production contract '{simpleName}'. Task 6.1 intentionally stays red until the adapter platform is implemented."),
            _ => throw new XunitException($"Production contract '{simpleName}' is ambiguous: {string.Join(", ", matches.Select(type => type.FullName))}")
        };
    }

    public static PropertyInfo RequireProperty(
        Type declaringType,
        string name,
        Func<PropertyInfo, bool> predicate)
    {
        var property = declaringType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.True(predicate(property), $"{declaringType.Name}.{name} has an invalid contract type.");
        return property;
    }

    public static MethodInfo RequireMethod(Type declaringType, string name)
    {
        var methods = declaringType.GetMethods()
            .Where(method => method.Name.Equals(name, StringComparison.Ordinal))
            .ToArray();
        return Assert.Single(methods);
    }

    public static FakeAdapterHandle CreateFakeAdapter(
        Type adapterType,
        SearchProviderEnum kind,
        string adapterVersion)
    {
        var adapter = DispatchProxy.Create(adapterType, typeof(FakeProviderDispatchProxy));
        var proxy = Assert.IsAssignableFrom<FakeProviderDispatchProxy>(adapter);
        proxy.Probe = new FakeProviderProbe(kind, adapterVersion);
        return new FakeAdapterHandle(adapter, proxy.Probe);
    }

    public static object CreateRegistry(Type adapterType, params object[] adapters)
    {
        var registryType = ProductionAssemblies
            .SelectMany(GetLoadableTypes)
            .Where(type =>
                !type.IsAbstract &&
                !type.IsInterface &&
                (type.Name.Equals("SearchProviderAdapterRegistry", StringComparison.Ordinal) ||
                 type.Name.Equals("ProviderAdapterRegistry", StringComparison.Ordinal) ||
                 type.Name.Equals("AdapterRegistry", StringComparison.Ordinal)))
            .Distinct()
            .SingleOrDefault()
            ?? throw new XunitException("Missing DI-backed SearchProviderAdapterRegistry production implementation.");

        var services = new ServiceCollection();
        foreach (var adapter in adapters)
        {
            services.AddSingleton(adapterType, adapter);
        }

        services.AddSingleton(registryType);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService(registryType);
    }

    public static object ResolveAdapter(object registry, SearchProviderEnum kind)
    {
        var method = registry.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(candidate =>
                candidate.GetParameters().Length == 1 &&
                candidate.GetParameters()[0].ParameterType.IsEnum &&
                (candidate.Name.Equals("GetRequired", StringComparison.Ordinal) ||
                 candidate.Name.Equals("GetRequiredAdapter", StringComparison.Ordinal) ||
                 candidate.Name.Equals("Resolve", StringComparison.Ordinal) ||
                 candidate.Name.Equals("GetAdapter", StringComparison.Ordinal) ||
                 candidate.Name.Equals("Get", StringComparison.Ordinal)))
            .SingleOrDefault()
            ?? throw new XunitException("Adapter registry must expose one fail-closed Provider Kind resolution method.");

        var argument = Enum.ToObject(method.GetParameters()[0].ParameterType, (int)kind);
        try
        {
            return method.Invoke(registry, [argument])
                   ?? throw new XunitException("Adapter registry returned null instead of failing closed.");
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    public static object ParseCapability(Type capabilityType, string name) =>
        Enum.Parse(capabilityType, name);

    public static object CombineFlags(Type capabilityType, params object[] flags)
    {
        var combined = flags.Aggregate(0UL, (current, flag) => current | Convert.ToUInt64(flag));
        return Enum.ToObject(capabilityType, combined);
    }

    public static bool HasFlag(object value, object flag) =>
        (Convert.ToUInt64(value) & Convert.ToUInt64(flag)) == Convert.ToUInt64(flag);

    public static object EvaluateCapabilities(
        object declared,
        object discovered,
        bool discoverySucceeded)
    {
        var capabilityType = declared.GetType();
        var method = ProductionAssemblies
            .SelectMany(GetLoadableTypes)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(candidate =>
            {
                var parameters = candidate.GetParameters();
                return candidate.ReturnType == capabilityType &&
                       parameters.Length == 3 &&
                       parameters[0].ParameterType == capabilityType &&
                       parameters[1].ParameterType == capabilityType &&
                       parameters[2].ParameterType == typeof(bool) &&
                       (candidate.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase) ||
                        candidate.Name.Contains("Intersect", StringComparison.OrdinalIgnoreCase));
            })
            .DistinctBy(candidate => $"{candidate.DeclaringType?.FullName}.{candidate.Name}")
            .SingleOrDefault()
            ?? throw new XunitException(
                "Missing capability evaluation contract accepting declared capabilities, discovered capabilities, and discovery success state.");

        var target = method.IsStatic
            ? null
            : Activator.CreateInstance(method.DeclaringType!)
              ?? throw new XunitException($"Could not create {method.DeclaringType!.Name} capability evaluator.");
        return method.Invoke(target, [declared, discovered, discoverySucceeded])
               ?? throw new XunitException("Capability evaluation returned null.");
    }

    public static bool TypeGraphContains(Type root, Type expected)
    {
        var visited = new HashSet<Type>();
        return Visit(root);

        bool Visit(Type current)
        {
            if (!visited.Add(current))
            {
                return false;
            }

            if (current == expected)
            {
                return true;
            }

            if (current.IsArray && current.GetElementType() is { } element && Visit(element))
            {
                return true;
            }

            return current.IsGenericType && current.GetGenericArguments().Any(Visit);
        }
    }

    public static IEnumerable<string> FlattenPublicContract(Type root, int depth)
    {
        var visited = new HashSet<Type>();
        return Flatten(root, depth);

        IEnumerable<string> Flatten(Type current, int remainingDepth)
        {
            if (remainingDepth < 0 || !visited.Add(current))
            {
                yield break;
            }

            yield return current.Name;
            foreach (var property in current.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                yield return property.Name;
                foreach (var nested in Flatten(property.PropertyType, remainingDepth - 1))
                {
                    yield return nested;
                }
            }
        }
    }

    public static void ValidateAdapterKind(object adapter, SearchProviderInstance instance)
    {
        var adapterType = adapter.GetType().GetInterfaces()
            .Single(candidate => candidate.Name.Equals("ISearchProviderAdapter", StringComparison.Ordinal));
        var kindProperty = adapterType.GetProperty("ProviderKind")
                           ?? throw new XunitException("Adapter ProviderKind metadata is missing.");
        var adapterKind = kindProperty.GetValue(adapter)
                          ?? throw new XunitException("Adapter ProviderKind metadata returned null.");
        var adapterKindType = adapterKind.GetType();

        var method = ProductionAssemblies
            .SelectMany(GetLoadableTypes)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 2 &&
                       parameters.All(parameter => parameter.ParameterType == adapterKindType) &&
                       (candidate.Name.Contains("Kind", StringComparison.OrdinalIgnoreCase) ||
                        candidate.Name.Contains("Match", StringComparison.OrdinalIgnoreCase));
            })
            .DistinctBy(candidate => $"{candidate.DeclaringType?.FullName}.{candidate.Name}")
            .SingleOrDefault()
            ?? throw new XunitException("Missing pre-traffic adapter/Provider Instance kind-mismatch guard.");

        var instanceKind = Enum.ToObject(adapterKindType, (int)instance.ProviderKind);
        var target = method.IsStatic
            ? null
            : Activator.CreateInstance(method.DeclaringType!)
              ?? throw new XunitException($"Could not create {method.DeclaringType!.Name} kind guard.");
        try
        {
            method.Invoke(target, [adapterKind, instanceKind]);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            return error.Types.OfType<Type>();
        }
    }
}

internal sealed record FakeAdapterHandle(object Adapter, FakeProviderProbe Probe);

internal sealed class FakeProviderProbe(SearchProviderEnum kind, string adapterVersion)
{
    public SearchProviderEnum Kind { get; } = kind;
    public string AdapterVersion { get; } = adapterVersion;
    public int OperationInvocations { get; set; }
}

internal class FakeProviderDispatchProxy : DispatchProxy
{
    public FakeProviderProbe Probe { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        return targetMethod.Name switch
        {
            "get_ProviderKind" => Enum.ToObject(targetMethod.ReturnType, (int)Probe.Kind),
            "get_AdapterVersion" => Probe.AdapterVersion,
            "get_DeclaredCapabilities" => Enum.ToObject(targetMethod.ReturnType, 0),
            "get_SettingsSchema" => CreateDefault(targetMethod.ReturnType),
            _ => InvokeOperation(targetMethod.ReturnType)
        };
    }

    private object? InvokeOperation(Type returnType)
    {
        Probe.OperationInvocations++;
        return CreateDefault(returnType);
    }

    private static object? CreateDefault(Type type)
    {
        if (type == typeof(void))
        {
            return null;
        }

        if (type == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var valueType = type.GetGenericArguments()[0];
            var value = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
            return typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(valueType)
                .Invoke(null, [value]);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            return typeof(FakeProviderDispatchProxy)
                .GetMethod(nameof(EmptyAsync), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(type.GetGenericArguments()[0])
                .Invoke(null, null);
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static async IAsyncEnumerable<T> EmptyAsync<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
