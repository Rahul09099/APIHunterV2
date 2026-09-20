using System.Collections;
using System.Net;
using System.Reflection;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Services;
using Xunit;
using Xunit.Sdk;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Expected-red provider Endpoint Policy and SSRF contract for Task 6.3.
/// Reflection keeps the test project compile-clean until Task 6.4 introduces the policy.
/// These tests deliberately describe observable security behavior rather than reusing the
/// unrelated server-credential scanner's DNS helper.
///
/// **Validates: Requirements 12.1-12.27, 17.11, 17.21, 17.42**
/// </summary>
public sealed class EndpointPolicySsrfTests
{
    private static readonly Guid InstanceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid InstanceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    /// <summary>
    /// **Validates: Requirements 12.1, 12.24**
    /// </summary>
    [Fact]
    public void Contract_GatesEverySelfHostedOperationAndCarriesAllRequiredBounds()
    {
        var policyType = EndpointPolicyProbe.RequirePolicyType();
        var operationType = EndpointPolicyProbe.RequireType("EndpointOperationKind");

        Assert.True(operationType.IsEnum);
        Assert.All(
            new[] { "CapabilityDiscovery", "CredentialValidation", "Search", "ContentRetrieval" },
            name => Assert.Contains(name, Enum.GetNames(operationType)));

        EndpointPolicyProbe.RequireMethod(policyType, "ValidateForOperationAsync");

        var optionsType = EndpointPolicyProbe.RequireType("EndpointPolicyOptions");
        var publicContract = string.Join('|', optionsType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name));

        Assert.Contains("Dns", publicContract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redirect", publicContract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Timeout", publicContract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ResponseSize", publicContract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ContentType", publicContract, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **Validates: Requirements 12.2**
    /// </summary>
    [Theory]
    [InlineData("HTTPS://EXAMPLE.COM:443/api/v4/", "https", "example.com", 443, "/api/v4")]
    [InlineData("https://Example.COM/api/../api/v1", "https", "example.com", 443, "/api/v1")]
    [InlineData("https://example.com:8443/api//v1/", "https", "example.com", 8443, "/api/v1")]
    public void UrlNormalization_ProducesCanonicalSchemeHostPortAndBasePath(
        string raw,
        string expectedScheme,
        string expectedHost,
        int expectedPort,
        string expectedBasePath)
    {
        var result = EndpointPolicyProbe.Normalize(raw, InstanceA, developmentHttpInstance: null);

        Assert.True(result.Allowed, result.Reason);
        Assert.Equal(expectedScheme, result.Scheme);
        Assert.Equal(expectedHost, result.Host);
        Assert.Equal(expectedPort, result.Port);
        Assert.Equal(expectedBasePath, result.BasePath);
    }

    /// <summary>
    /// **Validates: Requirements 12.3-12.6**
    /// </summary>
    [Theory]
    [InlineData("https://user:password@example.com/api")]
    [InlineData("https://example.com/api#fragment")]
    [InlineData("https://example.com/api?unexpected=true")]
    [InlineData("ftp://example.com/api")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//example.com/api")]
    [InlineData("not a url")]
    public void UrlValidation_RejectsUserInfoFragmentQueryUnsupportedSchemeAndMalformedInput(string raw)
    {
        var result = EndpointPolicyProbe.Normalize(raw, InstanceA, developmentHttpInstance: null);

        Assert.False(result.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    /// <summary>
    /// **Validates: Requirements 12.6-12.8**
    /// </summary>
    [Fact]
    public void Http_IsAllowedOnlyForTheExactlyNamedDevelopmentInstance()
    {
        var deniedByDefault = EndpointPolicyProbe.Normalize(
            "http://dev-provider.example.test/api",
            InstanceA,
            developmentHttpInstance: null);
        var deniedForAnotherInstance = EndpointPolicyProbe.Normalize(
            "http://dev-provider.example.test/api",
            InstanceA,
            developmentHttpInstance: InstanceB);
        var allowedForNamedInstance = EndpointPolicyProbe.Normalize(
            "http://dev-provider.example.test/api",
            InstanceA,
            developmentHttpInstance: InstanceA);

        Assert.False(deniedByDefault.Allowed);
        Assert.False(deniedForAnotherInstance.Allowed);
        Assert.True(allowedForNamedInstance.Allowed, allowedForNamedInstance.Reason);
        Assert.Equal("http", allowedForNamedInstance.Scheme);
        Assert.Equal(80, allowedForNamedInstance.Port);
    }

    /// <summary>
    /// Property 11 address-classification examples.
    /// **Validates: Requirements 12.11-12.13, 17.42**
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0", "IPv4 unspecified")]
    [InlineData("10.0.0.1", "IPv4 private 10/8")]
    [InlineData("172.16.0.1", "IPv4 private 172.16/12")]
    [InlineData("192.168.1.1", "IPv4 private 192.168/16")]
    [InlineData("127.0.0.1", "IPv4 loopback")]
    [InlineData("169.254.1.1", "IPv4 link-local")]
    [InlineData("169.254.169.254", "metadata service")]
    [InlineData("224.0.0.1", "IPv4 multicast")]
    [InlineData("::", "IPv6 unspecified")]
    [InlineData("::1", "IPv6 loopback")]
    [InlineData("fe80::1", "IPv6 link-local")]
    [InlineData("fc00::1", "IPv6 unique-local")]
    [InlineData("ff02::1", "IPv6 multicast")]
    [InlineData("::ffff:127.0.0.1", "IPv4-mapped loopback")]
    [InlineData("::ffff:169.254.169.254", "IPv4-mapped metadata")]
    public void AddressClassification_BlocksEveryRequiredIpv4AndIpv6Class(
        string address,
        string classification)
    {
        var allowed = EndpointPolicyProbe.IsAddressAllowed(
            IPAddress.Parse(address),
            InstanceA,
            EndpointPolicyProbe.NoPrivateAllowlists);

        Assert.False(allowed, $"{classification} address {address} must be blocked by default.");
    }

    /// <summary>
    /// **Validates: Requirements 12.9-12.10, 12.15**
    /// </summary>
    [Fact]
    public void DnsValidation_RejectsTheWholeResolutionWhenAnyReturnedAddressIsBlocked()
    {
        var mixedResolution = new[]
        {
            IPAddress.Parse("8.8.8.8"),
            IPAddress.Parse("127.0.0.1"),
            IPAddress.Parse("2606:4700:4700::1111")
        };

        var result = EndpointPolicyProbe.ValidateResolvedAddresses(
            InstanceA,
            mixedResolution,
            EndpointPolicyProbe.NoPrivateAllowlists);

        Assert.False(result.Allowed);
        Assert.Empty(result.ValidatedAddresses);
    }

    /// <summary>
    /// **Validates: Requirements 12.13-12.15, 17.42**
    /// </summary>
    [Fact]
    public void PrivateAllowlist_IsScopedToOneProviderInstanceAndDoesNotPermitOtherBlockedClasses()
    {
        var allowlists = EndpointPolicyProbe.PrivateAllowlists(
            (InstanceA, new[] { "10.20.0.0/16" }));
        var privateAddress = IPAddress.Parse("10.20.30.40");

        Assert.True(EndpointPolicyProbe.IsAddressAllowed(privateAddress, InstanceA, allowlists));
        Assert.False(EndpointPolicyProbe.IsAddressAllowed(privateAddress, InstanceB, allowlists));
        Assert.False(EndpointPolicyProbe.IsAddressAllowed(
            IPAddress.Parse("127.0.0.1"),
            InstanceA,
            allowlists));
        Assert.False(EndpointPolicyProbe.IsAddressAllowed(
            IPAddress.Parse("169.254.169.254"),
            InstanceA,
            allowlists));
    }

    /// <summary>
    /// Property 11 connection binding.
    /// **Validates: Requirements 12.16, 17.11**
    /// </summary>
    [Fact]
    public void ConnectionPlan_ConnectsToValidatedAddressWhilePreservingHostAndTlsSni()
    {
        var validatedAddress = IPAddress.Parse("203.0.113.10");
        var plan = EndpointPolicyProbe.CreateConnectionPlan(
            "git.example.test",
            443,
            validatedAddress);

        Assert.Equal(validatedAddress, plan.ConnectAddress);
        Assert.Equal(443, plan.ConnectPort);
        Assert.Equal("git.example.test", plan.HostHeader);
        Assert.Equal("git.example.test", plan.SniHost);
    }

    /// <summary>
    /// **Validates: Requirements 12.9, 12.15-12.17, 17.21, 17.42**
    /// </summary>
    [Fact]
    public void DnsRebinding_RevalidatesEveryConnectionAndBlocksAChangedPrivateAddress()
    {
        var firstConnection = EndpointPolicyProbe.ValidateResolvedAddresses(
            InstanceA,
            new[] { IPAddress.Parse("8.8.8.8") },
            EndpointPolicyProbe.NoPrivateAllowlists);
        var secondConnection = EndpointPolicyProbe.ValidateResolvedAddresses(
            InstanceA,
            new[] { IPAddress.Parse("127.0.0.1") },
            EndpointPolicyProbe.NoPrivateAllowlists);

        Assert.True(firstConnection.Allowed, firstConnection.Reason);
        Assert.False(secondConnection.Allowed);
        Assert.Empty(secondConnection.ValidatedAddresses);
    }

    /// <summary>
    /// **Validates: Requirements 12.18-12.19, 17.42**
    /// </summary>
    [Fact]
    public void Redirects_EnforceMaximumAndRevalidateEveryTargetAddress()
    {
        var headers = EndpointPolicyProbe.SensitiveHeaders();
        var approvedOrigin = new Uri("https://git.example.test:443");

        var overLimit = EndpointPolicyProbe.ValidateRedirect(
            approvedOrigin,
            "/api/v1",
            new Uri("https://git.example.test/api/v1/page/2"),
            redirectCount: 4,
            maxRedirects: 3,
            headers,
            new[] { IPAddress.Parse("8.8.8.8") },
            InstanceA,
            EndpointPolicyProbe.NoPrivateAllowlists);
        var blockedTarget = EndpointPolicyProbe.ValidateRedirect(
            approvedOrigin,
            "/api/v1",
            new Uri("https://redirect.example.test/api/v1/page/2"),
            redirectCount: 1,
            maxRedirects: 3,
            headers,
            new[] { IPAddress.Parse("127.0.0.1") },
            InstanceA,
            EndpointPolicyProbe.NoPrivateAllowlists);

        Assert.False(overLimit.Allowed);
        Assert.False(blockedTarget.Allowed);
    }

    /// <summary>
    /// Property 11 redirect credential stripping.
    /// **Validates: Requirements 12.20-12.21, 17.11, 17.42**
    /// </summary>
    [Fact]
    public void CrossOriginRedirect_StripsCredentialNodeCookieAndAuthorizationHeaders()
    {
        var decision = EndpointPolicyProbe.ValidateRedirect(
            new Uri("https://git.example.test:443"),
            "/api/v1",
            new Uri("https://cdn.example.test/api/v1/page/2"),
            redirectCount: 1,
            maxRedirects: 3,
            EndpointPolicyProbe.SensitiveHeaders(),
            new[] { IPAddress.Parse("8.8.8.8") },
            InstanceA,
            EndpointPolicyProbe.NoPrivateAllowlists);

        Assert.True(decision.Allowed, decision.Reason);
        Assert.DoesNotContain(decision.ForwardedHeaders.Keys,
            key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(decision.ForwardedHeaders.Keys,
            key => key.Equals("PRIVATE-TOKEN", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(decision.ForwardedHeaders.Keys,
            key => key.Equals("X-Node-Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(decision.ForwardedHeaders.Keys,
            key => key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("trace-value", decision.ForwardedHeaders["X-Trace"]);
    }

    /// <summary>
    /// **Validates: Requirements 12.22-12.23, 17.11**
    /// </summary>
    [Theory]
    [InlineData("http://git.example.test/api/v1/search")]
    [InlineData("https://other.example.test/api/v1/search")]
    [InlineData("https://git.example.test:8443/api/v1/search")]
    [InlineData("https://git.example.test/not-approved/search")]
    [InlineData("https://git.example.test/api/v10/looks-similar")]
    public void CredentialBinding_RejectsAnySchemeHostPortOrPathPrefixMismatch(string target)
    {
        var result = EndpointPolicyProbe.ValidateRequestBinding(
            new Uri("https://git.example.test:443"),
            "/api/v1",
            new Uri(target));

        Assert.False(result.Allowed);
        Assert.False(result.MayAttachCredential);
    }

    /// <summary>
    /// **Validates: Requirements 12.22-12.24**
    /// </summary>
    [Fact]
    public void CredentialBindingAndResponseBounds_AllowOnlyExactBoundedRequests()
    {
        var binding = EndpointPolicyProbe.ValidateRequestBinding(
            new Uri("https://git.example.test:443"),
            "/api/v1",
            new Uri("https://git.example.test/api/v1/search"));
        var oversized = EndpointPolicyProbe.ValidateResponse(
            contentLength: 1_048_577,
            contentType: "application/json",
            maxResponseSize: 1_048_576,
            allowedContentTypes: new[] { "application/json" },
            elapsed: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(10));
        var wrongType = EndpointPolicyProbe.ValidateResponse(
            contentLength: 100,
            contentType: "text/html",
            maxResponseSize: 1_048_576,
            allowedContentTypes: new[] { "application/json" },
            elapsed: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(10));
        var timedOut = EndpointPolicyProbe.ValidateResponse(
            contentLength: 100,
            contentType: "application/json",
            maxResponseSize: 1_048_576,
            allowedContentTypes: new[] { "application/json" },
            elapsed: TimeSpan.FromSeconds(11),
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(binding.Allowed, binding.Reason);
        Assert.True(binding.MayAttachCredential);
        Assert.False(oversized.Allowed);
        Assert.False(wrongType.Allowed);
        Assert.False(timedOut.Allowed);
    }

    /// <summary>
    /// **Validates: Requirements 12.25-12.26**
    /// </summary>
    [Fact]
    public void ValidationFailure_DisablesNewOperationsAndNeverRetriesTheRawUrl()
    {
        var decision = EndpointPolicyProbe.CreateFailureDecision("blocked address");

        Assert.False(decision.Allowed);
        Assert.True(decision.DisableNewOperations);
        Assert.False(decision.RetryRawUrl);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    /// <summary>
    /// **Validates: Requirements 2.26, 12.27**
    /// </summary>
    [Fact]
    public void ApprovalInvalidation_TriggersForOriginOrPathChangesButNotDisplayOnlyChanges()
    {
        var baseline = new EndpointIdentity("https", "git.example.test", 443, "/api/v1");

        Assert.True(EndpointPolicyProbe.HasEndpointIdentityChanged(
            baseline,
            baseline with { Host = "new.example.test" }));
        Assert.True(EndpointPolicyProbe.HasEndpointIdentityChanged(
            baseline,
            baseline with { Port = 8443 }));
        Assert.True(EndpointPolicyProbe.HasEndpointIdentityChanged(
            baseline,
            baseline with { BasePath = "/api/v2" }));
        Assert.False(EndpointPolicyProbe.HasEndpointIdentityChanged(baseline, baseline));

        var providerInstance = new SearchProviderInstance
        {
            StableId = InstanceA,
            NormalizedScheme = baseline.Scheme,
            NormalizedHost = baseline.Host,
            NormalizedPort = baseline.Port,
            NormalizedBasePath = baseline.BasePath,
            ApprovedByTelegramId = 42
        };
        var invalidated = EndpointPolicyProbe.InvalidateApproval(
            providerInstance,
            baseline with { BasePath = "/api/v2" });

        Assert.True(invalidated);
        Assert.Null(providerInstance.ApprovedByTelegramId);
    }
}

internal static class EndpointPolicyProbe
{
    private static readonly Assembly[] ProductionAssemblies =
    [
        typeof(SearchProviderInstance).Assembly,
        typeof(ISearchProviderAdapter).Assembly,
        typeof(ScraperService).Assembly
    ];

    public static IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> NoPrivateAllowlists { get; } =
        new Dictionary<Guid, IReadOnlyCollection<string>>();

    public static Type RequirePolicyType() => RequireType("EndpointPolicy");

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
                $"Missing production contract '{simpleName}'. Task 6.3 intentionally remains red until Endpoint Policy is implemented."),
            _ => throw new XunitException(
                $"Production contract '{simpleName}' is ambiguous: {string.Join(", ", matches.Select(type => type.FullName))}")
        };
    }

    public static MethodInfo RequireMethod(Type type, string name) =>
        Assert.Single(
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
            method => method.Name.Equals(name, StringComparison.Ordinal));

    public static EndpointDecision Normalize(
        string raw,
        Guid instanceStableId,
        Guid? developmentHttpInstance)
    {
        var value = Invoke("NormalizeAndValidate", raw, instanceStableId, developmentHttpInstance);
        return ReadEndpointDecision(value);
    }

    public static bool IsAddressAllowed(
        IPAddress address,
        Guid instanceStableId,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> allowlists) =>
        ReadBoolean(Invoke("IsAddressAllowed", address, instanceStableId, allowlists));

    public static EndpointDecision ValidateResolvedAddresses(
        Guid instanceStableId,
        IReadOnlyCollection<IPAddress> addresses,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> allowlists)
    {
        var value = Invoke("ValidateResolvedAddresses", instanceStableId, addresses, allowlists);
        var decision = ReadEndpointDecision(value);
        var validated = ReadProperty(value, "ValidatedAddresses", "Addresses") as IEnumerable;
        return decision with
        {
            ValidatedAddresses = validated?.Cast<object>()
                .Select(item => Assert.IsType<IPAddress>(item))
                .ToArray() ?? []
        };
    }

    public static ConnectionPlan CreateConnectionPlan(string host, int port, IPAddress address)
    {
        var value = Invoke("CreateConnectionPlan", host, port, address);
        return new ConnectionPlan(
            Assert.IsType<IPAddress>(ReadProperty(value, "ConnectAddress", "Address")),
            Convert.ToInt32(ReadProperty(value, "ConnectPort", "Port")),
            Assert.IsType<string>(ReadProperty(value, "HostHeader", "Host")),
            Assert.IsType<string>(ReadProperty(value, "SniHost", "TlsServerName", "ServerName")));
    }

    public static RedirectDecision ValidateRedirect(
        Uri approvedOrigin,
        string approvedBasePath,
        Uri redirectTarget,
        int redirectCount,
        int maxRedirects,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyCollection<IPAddress> resolvedAddresses,
        Guid instanceStableId,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> allowlists)
    {
        var value = Invoke(
            "ValidateRedirect",
            approvedOrigin,
            approvedBasePath,
            redirectTarget,
            redirectCount,
            maxRedirects,
            headers,
            resolvedAddresses,
            instanceStableId,
            allowlists);
        var decision = ReadEndpointDecision(value);
        return new RedirectDecision(
            decision.Allowed,
            decision.Reason,
            ReadStringDictionary(ReadProperty(value, "ForwardedHeaders", "Headers")));
    }

    public static RequestBindingDecision ValidateRequestBinding(
        Uri approvedOrigin,
        string approvedBasePath,
        Uri target)
    {
        var value = Invoke("ValidateRequestBinding", approvedOrigin, approvedBasePath, target);
        var decision = ReadEndpointDecision(value);
        return new RequestBindingDecision(
            decision.Allowed,
            decision.Reason,
            ReadBoolean(ReadProperty(value, "MayAttachCredential", "CredentialAllowed")));
    }

    public static EndpointDecision ValidateResponse(
        long contentLength,
        string contentType,
        long maxResponseSize,
        IReadOnlyCollection<string> allowedContentTypes,
        TimeSpan elapsed,
        TimeSpan timeout) =>
        ReadEndpointDecision(Invoke(
            "ValidateResponse",
            contentLength,
            contentType,
            maxResponseSize,
            allowedContentTypes,
            elapsed,
            timeout));

    public static FailureDecision CreateFailureDecision(string reason)
    {
        var value = Invoke("CreateValidationFailure", reason);
        var decision = ReadEndpointDecision(value);
        return new FailureDecision(
            decision.Allowed,
            decision.Reason,
            ReadBoolean(ReadProperty(value, "DisableNewOperations")),
            ReadBoolean(ReadProperty(value, "RetryRawUrl")));
    }

    public static bool HasEndpointIdentityChanged(EndpointIdentity current, EndpointIdentity candidate) =>
        ReadBoolean(Invoke(
            "HasEndpointIdentityChanged",
            current.Scheme,
            current.Host,
            current.Port,
            current.BasePath,
            candidate.Scheme,
            candidate.Host,
            candidate.Port,
            candidate.BasePath));

    public static bool InvalidateApproval(SearchProviderInstance instance, EndpointIdentity candidate) =>
        ReadBoolean(Invoke(
            "InvalidateApprovalIfEndpointChanged",
            instance,
            candidate.Scheme,
            candidate.Host,
            candidate.Port,
            candidate.BasePath));

    public static IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> PrivateAllowlists(
        params (Guid InstanceStableId, IReadOnlyCollection<string> Networks)[] entries) =>
        entries.ToDictionary(entry => entry.InstanceStableId, entry => entry.Networks);

    public static IReadOnlyDictionary<string, string> SensitiveHeaders() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer provider-secret",
            ["PRIVATE-TOKEN"] = "provider-secret",
            ["X-Node-Token"] = "node-secret",
            ["Cookie"] = "session=secret",
            ["X-Trace"] = "trace-value"
        };

    private static object Invoke(string methodName, params object?[] arguments)
    {
        var policyType = RequirePolicyType();
        var candidates = policyType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(method =>
                method.Name.Equals(methodName, StringComparison.Ordinal) &&
                method.GetParameters().Length == arguments.Length)
            .ToArray();
        var method = Assert.Single(candidates);
        var target = method.IsStatic
            ? null
            : Activator.CreateInstance(policyType)
              ?? throw new XunitException($"{policyType.Name} must expose a parameterless constructor or static policy operations for contract tests.");

        try
        {
            return method.Invoke(target, arguments)
                   ?? throw new XunitException($"{policyType.Name}.{methodName} returned null.");
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    private static EndpointDecision ReadEndpointDecision(object value) => new(
        ReadBoolean(ReadProperty(value, "Allowed", "IsAllowed", "Succeeded", "IsValid")),
        ReadOptionalString(value, "Reason", "FailureReason", "Error"),
        ReadOptionalString(value, "Scheme", "NormalizedScheme"),
        ReadOptionalString(value, "Host", "NormalizedHost"),
        ReadOptionalInt(value, "Port", "NormalizedPort"),
        ReadOptionalString(value, "BasePath", "NormalizedBasePath"),
        []);

    private static object ReadProperty(object value, params string[] names)
    {
        var property = names
            .Select(name => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public))
            .FirstOrDefault(candidate => candidate is not null)
            ?? throw new XunitException(
                $"{value.GetType().Name} must expose one of: {string.Join(", ", names)}.");
        return property.GetValue(value)
               ?? throw new XunitException($"{value.GetType().Name}.{property.Name} returned null.");
    }

    private static bool ReadBoolean(object value) => Assert.IsType<bool>(value);

    private static string ReadOptionalString(object value, params string[] names)
    {
        var property = names
            .Select(name => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public))
            .FirstOrDefault(candidate => candidate is not null);
        return property?.GetValue(value) as string ?? string.Empty;
    }

    private static int ReadOptionalInt(object value, params string[] names)
    {
        var property = names
            .Select(name => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public))
            .FirstOrDefault(candidate => candidate is not null);
        return property?.GetValue(value) is { } raw ? Convert.ToInt32(raw) : 0;
    }

    private static IReadOnlyDictionary<string, string> ReadStringDictionary(object value)
    {
        if (value is IReadOnlyDictionary<string, string> typed)
        {
            return typed;
        }

        if (value is IDictionary dictionary)
        {
            return dictionary.Keys.Cast<object>().ToDictionary(
                key => Convert.ToString(key)!,
                key => Convert.ToString(dictionary[key])!,
                StringComparer.OrdinalIgnoreCase);
        }

        throw new XunitException($"Expected a string header dictionary, got {value.GetType().Name}.");
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

internal sealed record EndpointDecision(
    bool Allowed,
    string Reason,
    string Scheme,
    string Host,
    int Port,
    string BasePath,
    IReadOnlyCollection<IPAddress> ValidatedAddresses);

internal sealed record ConnectionPlan(
    IPAddress ConnectAddress,
    int ConnectPort,
    string HostHeader,
    string SniHost);

internal sealed record RedirectDecision(
    bool Allowed,
    string Reason,
    IReadOnlyDictionary<string, string> ForwardedHeaders);

internal sealed record RequestBindingDecision(bool Allowed, string Reason, bool MayAttachCredential);

internal sealed record FailureDecision(
    bool Allowed,
    string Reason,
    bool DisableNewOperations,
    bool RetryRawUrl);

internal sealed record EndpointIdentity(string Scheme, string Host, int Port, string BasePath);
