using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

public enum EndpointOperationKind
{
    CapabilityDiscovery,
    CredentialValidation,
    Search,
    ContentRetrieval
}

public sealed class EndpointPolicyOptions
{
    public TimeSpan DnsCacheLifetime { get; init; } = TimeSpan.Zero;
    public int MaxRedirects { get; init; } = 3;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public long MaxResponseSizeBytes { get; init; } = 4 * 1024 * 1024;
    public IReadOnlySet<string> AllowedContentTypes { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "application/json",
            "application/octet-stream",
            "text/plain"
        };
    public int MaxEventCount { get; init; } = 10_000;
    public int MaxContentConcurrency { get; init; } = 4;
}

public interface IEndpointDnsResolver
{
    Task<IReadOnlyCollection<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken = default);
}

public sealed class EndpointDnsResolver : IEndpointDnsResolver
{
    public async Task<IReadOnlyCollection<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken = default) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}

public sealed record EndpointPolicyDecision(
    bool Allowed,
    string Reason,
    string Scheme = "",
    string Host = "",
    int Port = 0,
    string BasePath = "",
    IReadOnlyCollection<IPAddress>? ValidatedAddresses = null,
    bool DisableNewOperations = false,
    bool RetryRawUrl = false);

public sealed record EndpointConnectionPlan(
    IPAddress ConnectAddress,
    int ConnectPort,
    string HostHeader,
    string SniHost);

public sealed record EndpointRedirectDecision(
    bool Allowed,
    string Reason,
    IReadOnlyDictionary<string, string> ForwardedHeaders);

public sealed record EndpointRequestBindingDecision(
    bool Allowed,
    string Reason,
    bool MayAttachCredential);

public sealed record ValidatedEndpointOperation(
    EndpointOperationKind Operation,
    ValidatedProviderInstance ProviderInstance,
    ProviderOperationBounds Bounds,
    IReadOnlyCollection<IPAddress> ValidatedAddresses);

/// <summary>
/// Central fail-closed origin, DNS, address, redirect, credential-binding, and response policy.
/// Approval persists policy identity; DNS is deliberately re-resolved for each operation so
/// an approval can never pin a stale safe answer across DNS rebinding.
/// </summary>
public sealed class EndpointPolicy
{
    public const int CurrentPolicyVersion = 1;

    private static readonly HashSet<string> SensitiveRedirectHeaders = new(
        ["Authorization", "PRIVATE-TOKEN", "X-Node-Token", "Cookie"],
        StringComparer.OrdinalIgnoreCase);

    private readonly IEndpointDnsResolver dnsResolver;
    private readonly EndpointPolicyOptions options;

    public EndpointPolicy()
        : this(new EndpointDnsResolver(), new EndpointPolicyOptions())
    {
    }

    public EndpointPolicy(IEndpointDnsResolver dnsResolver, EndpointPolicyOptions options)
    {
        this.dnsResolver = dnsResolver ?? throw new ArgumentNullException(nameof(dnsResolver));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ValidatedEndpointOperation> ValidateForOperationAsync(
        SearchProviderInstance instance,
        EndpointOperationKind operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!Enum.IsDefined(operation))
        {
            throw new EndpointPolicyException("Unsupported endpoint operation.");
        }

        var normalized = ValidateApprovedIdentity(instance);
        if (!normalized.Allowed)
        {
            throw new EndpointPolicyException(normalized.Reason);
        }

        IReadOnlyCollection<IPAddress> resolved;
        try
        {
            resolved = await dnsResolver.ResolveAsync(normalized.Host, cancellationToken);
        }
        catch (Exception error) when (error is SocketException or ArgumentException)
        {
            throw new EndpointPolicyException("Endpoint DNS resolution failed.");
        }

        var allowlists = new Dictionary<Guid, IReadOnlyCollection<string>>
        {
            [instance.StableId] = ParsePrivateNetworkAllowlist(instance.PrivateNetworkAllowlistJson)
        };
        var addressDecision = ValidateResolvedAddresses(instance.StableId, resolved, allowlists);
        if (!addressDecision.Allowed)
        {
            throw new EndpointPolicyException(addressDecision.Reason);
        }

        return new ValidatedEndpointOperation(
            operation,
            new ValidatedProviderInstance(
                instance.StableId,
                instance.ProviderKind,
                normalized.Scheme,
                normalized.Host,
                normalized.Port,
                normalized.BasePath,
                instance.SettingsVersion,
                instance.SettingsJson),
            new ProviderOperationBounds(
                options.MaxResponseSizeBytes,
                options.AllowedContentTypes,
                options.MaxEventCount,
                options.MaxContentConcurrency,
                options.RequestTimeout),
            addressDecision.ValidatedAddresses ?? []);
    }

    public static EndpointPolicyDecision ValidateApprovedIdentity(SearchProviderInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var raw = BuildAbsoluteEndpoint(
            instance.NormalizedScheme,
            instance.NormalizedHost,
            instance.NormalizedPort,
            instance.NormalizedBasePath);
        var normalized = NormalizeAndValidate(
            raw,
            instance.StableId,
            instance.DevelopmentHttpAllowed ? instance.StableId : null);
        if (!normalized.Allowed)
        {
            return CreateValidationFailure(normalized.Reason);
        }

        var canonicalIdentity = CreateCanonicalIdentity(
            normalized.Scheme,
            normalized.Host,
            normalized.Port,
            normalized.BasePath);
        if (instance.ApprovedByTelegramId is null ||
            instance.EndpointPolicyVersion != CurrentPolicyVersion ||
            instance.EndpointApprovedAtUtc is null ||
            !string.Equals(instance.ApprovedEndpointIdentity, canonicalIdentity, StringComparison.Ordinal))
        {
            return CreateValidationFailure("Endpoint policy is not approved for the current origin and path.");
        }

        try
        {
            _ = ParsePrivateNetworkAllowlist(instance.PrivateNetworkAllowlistJson);
        }
        catch (ArgumentException)
        {
            return CreateValidationFailure("The private-network allowlist is invalid.");
        }

        return normalized;
    }

    public static EndpointPolicyDecision NormalizeAndValidate(
        string raw,
        Guid instanceStableId,
        Guid? developmentHttpInstance)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return CreateValidationFailure("Endpoint must be an absolute URL.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return CreateValidationFailure("Endpoint user-info is not allowed.");
        }
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            return CreateValidationFailure("Endpoint fragments are not allowed.");
        }
        if (!string.IsNullOrEmpty(uri.Query))
        {
            return CreateValidationFailure("Endpoint query strings are not allowed.");
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not "https" and not "http")
        {
            return CreateValidationFailure("Only HTTPS endpoints are supported.");
        }
        if (scheme == "http" && developmentHttpInstance != instanceStableId)
        {
            return CreateValidationFailure("HTTP requires an explicit instance-scoped development exception.");
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            return CreateValidationFailure("Endpoint host is invalid.");
        }

        var port = uri.IsDefaultPort ? (scheme == "https" ? 443 : 80) : uri.Port;
        if (port is < 1 or > 65535)
        {
            return CreateValidationFailure("Endpoint port is invalid.");
        }

        string basePath;
        try
        {
            basePath = NormalizePath(uri.AbsolutePath);
        }
        catch (ArgumentException error)
        {
            return CreateValidationFailure(error.Message);
        }

        return new EndpointPolicyDecision(
            true,
            string.Empty,
            scheme,
            host,
            port,
            basePath,
            []);
    }

    public static bool IsAddressAllowed(
        IPAddress address,
        Guid instanceStableId,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> allowlists)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(allowlists);

        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var blocked = ClassifyBlockedAddress(candidate);
        if (blocked == BlockedAddressClass.None)
        {
            return true;
        }

        if (blocked != BlockedAddressClass.Private ||
            !allowlists.TryGetValue(instanceStableId, out var networks))
        {
            return false;
        }

        return networks.Any(network => IpNetwork.TryParse(network, out var parsed) && parsed.Contains(candidate));
    }

    public static EndpointPolicyDecision ValidateResolvedAddresses(
        Guid instanceStableId,
        IReadOnlyCollection<IPAddress> addresses,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> allowlists)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
        {
            return CreateValidationFailure("Endpoint DNS resolution returned no addresses.");
        }

        if (addresses.Any(address => !IsAddressAllowed(address, instanceStableId, allowlists)))
        {
            return CreateValidationFailure("Endpoint DNS resolution contains a blocked address.");
        }

        return new EndpointPolicyDecision(
            true,
            string.Empty,
            ValidatedAddresses: addresses.Distinct().ToArray());
    }

    public static EndpointConnectionPlan CreateConnectionPlan(string host, int port, IPAddress address)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            throw new ArgumentException("A valid endpoint host and port are required.");
        }

        return new EndpointConnectionPlan(address, port, host, host);
    }

    public static EndpointRedirectDecision ValidateRedirect(
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
        ArgumentNullException.ThrowIfNull(approvedOrigin);
        ArgumentNullException.ThrowIfNull(redirectTarget);
        ArgumentNullException.ThrowIfNull(headers);

        if (redirectCount < 0 || redirectCount > maxRedirects)
        {
            return new EndpointRedirectDecision(false, "Redirect limit exceeded.", EmptyHeaders());
        }

        var normalized = NormalizeAndValidate(redirectTarget.AbsoluteUri, instanceStableId, null);
        if (!normalized.Allowed)
        {
            return new EndpointRedirectDecision(false, normalized.Reason, EmptyHeaders());
        }

        var addressDecision = ValidateResolvedAddresses(instanceStableId, resolvedAddresses, allowlists);
        if (!addressDecision.Allowed)
        {
            return new EndpointRedirectDecision(false, addressDecision.Reason, EmptyHeaders());
        }

        var sameOrigin = OriginsEqual(approvedOrigin, redirectTarget);
        var forwarded = headers
            .Where(pair => sameOrigin || !SensitiveRedirectHeaders.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        return new EndpointRedirectDecision(true, string.Empty, forwarded);
    }

    public static EndpointRequestBindingDecision ValidateRequestBinding(
        Uri approvedOrigin,
        string approvedBasePath,
        Uri target)
    {
        ArgumentNullException.ThrowIfNull(approvedOrigin);
        ArgumentNullException.ThrowIfNull(target);

        if (!OriginsEqual(approvedOrigin, target))
        {
            return new EndpointRequestBindingDecision(false, "Request origin does not match approval.", false);
        }

        var normalizedApprovedPath = NormalizePath(approvedBasePath);
        var normalizedTargetPath = NormalizePath(target.AbsolutePath);
        var pathMatches = normalizedApprovedPath == "/" ||
                          normalizedTargetPath.Equals(normalizedApprovedPath, StringComparison.Ordinal) ||
                          normalizedTargetPath.StartsWith(normalizedApprovedPath + "/", StringComparison.Ordinal);
        return pathMatches
            ? new EndpointRequestBindingDecision(true, string.Empty, true)
            : new EndpointRequestBindingDecision(false, "Request path is outside the approved API prefix.", false);
    }

    public static EndpointPolicyDecision ValidateResponse(
        long contentLength,
        string contentType,
        long maxResponseSize,
        IReadOnlyCollection<string> allowedContentTypes,
        TimeSpan elapsed,
        TimeSpan timeout)
    {
        if (contentLength < 0 || maxResponseSize <= 0 || contentLength > maxResponseSize)
        {
            return CreateValidationFailure("Provider response exceeds the configured size bound.");
        }
        if (elapsed < TimeSpan.Zero || timeout <= TimeSpan.Zero || elapsed > timeout)
        {
            return CreateValidationFailure("Provider request exceeded the configured timeout.");
        }

        var mediaType = (contentType ?? string.Empty).Split(';', 2)[0].Trim();
        if (!allowedContentTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
        {
            return CreateValidationFailure("Provider response content type is not allowed.");
        }

        return new EndpointPolicyDecision(true, string.Empty);
    }

    public static EndpointPolicyDecision CreateValidationFailure(string reason) =>
        new(false, string.IsNullOrWhiteSpace(reason) ? "Endpoint validation failed." : reason,
            ValidatedAddresses: [], DisableNewOperations: true, RetryRawUrl: false);

    public static bool HasEndpointIdentityChanged(
        string currentScheme,
        string currentHost,
        int currentPort,
        string currentBasePath,
        string candidateScheme,
        string candidateHost,
        int candidatePort,
        string candidateBasePath) =>
        !string.Equals(
            CreateCanonicalIdentity(currentScheme, currentHost, currentPort, currentBasePath),
            CreateCanonicalIdentity(candidateScheme, candidateHost, candidatePort, candidateBasePath),
            StringComparison.Ordinal);

    public static bool InvalidateApprovalIfEndpointChanged(
        SearchProviderInstance instance,
        string candidateScheme,
        string candidateHost,
        int candidatePort,
        string candidateBasePath)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!HasEndpointIdentityChanged(
                instance.NormalizedScheme,
                instance.NormalizedHost,
                instance.NormalizedPort,
                instance.NormalizedBasePath,
                candidateScheme,
                candidateHost,
                candidatePort,
                candidateBasePath))
        {
            return false;
        }

        ClearApproval(instance);
        return true;
    }

    public static void ClearApproval(SearchProviderInstance instance)
    {
        instance.ApprovedByTelegramId = null;
        instance.EndpointPolicyVersion = 0;
        instance.ApprovedEndpointIdentity = null;
        instance.EndpointApprovedAtUtc = null;
    }

    public static string CreateCanonicalIdentity(
        string scheme,
        string host,
        int port,
        string basePath) =>
        $"{scheme.Trim().ToLowerInvariant()}://{host.Trim().TrimEnd('.').ToLowerInvariant()}:{port}{NormalizePath(basePath)}";

    public static IReadOnlyCollection<string> ParsePrivateNetworkAllowlist(string json)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<string[]>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? [];
            var normalized = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (!IpNetwork.TryParse(entry, out var network) ||
                    ClassifyBlockedAddress(network.NetworkAddress) != BlockedAddressClass.Private)
                {
                    throw new ArgumentException("Only valid private IPv4 or IPv6 CIDRs may be allowlisted.");
                }
                normalized.Add(network.ToString());
            }
            return normalized.ToArray();
        }
        catch (JsonException error)
        {
            throw new ArgumentException("Private-network allowlist must be a JSON string array.", nameof(json), error);
        }
    }

    public static string SerializePrivateNetworkAllowlist(IEnumerable<string> networks) =>
        JsonSerializer.Serialize(ParsePrivateNetworkAllowlist(JsonSerializer.Serialize(networks)));

    private static string BuildAbsoluteEndpoint(string scheme, string host, int port, string path)
    {
        var hostValue = host.Contains(':') && !host.StartsWith('[')
            ? $"[{host}]"
            : host;
        return $"{scheme}://{hostValue}:{port}{(path.StartsWith('/') ? path : "/" + path)}";
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "/";
        }

        var unescaped = Uri.UnescapeDataString(path.Replace('\\', '/'));
        var segments = new List<string>();
        foreach (var segment in unescaped.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                continue;
            }
            if (segment.Contains('\0'))
            {
                throw new ArgumentException("Endpoint path contains an invalid character.");
            }
            segments.Add(segment);
        }

        return segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }

    private static bool OriginsEqual(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.IdnHost.TrimEnd('.').Equals(right.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) &&
        EffectivePort(left) == EffectivePort(right);

    private static int EffectivePort(Uri uri) => uri.IsDefaultPort
        ? uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80
        : uri.Port;

    private static IReadOnlyDictionary<string, string> EmptyHeaders() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static BlockedAddressClass ClassifyBlockedAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168))
            {
                return BlockedAddressClass.Private;
            }
            if (bytes[0] == 0 || bytes[0] == 127 ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                bytes[0] >= 224)
            {
                return BlockedAddressClass.AlwaysBlocked;
            }
            return BlockedAddressClass.None;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return ClassifyBlockedAddress(address.MapToIPv4());
            }
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return BlockedAddressClass.Private;
            }
            if (IPAddress.IPv6Any.Equals(address) || IPAddress.IPv6Loopback.Equals(address) ||
                address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
            {
                return BlockedAddressClass.AlwaysBlocked;
            }
            return BlockedAddressClass.None;
        }

        return BlockedAddressClass.AlwaysBlocked;
    }

    private enum BlockedAddressClass
    {
        None,
        Private,
        AlwaysBlocked
    }

    private readonly record struct IpNetwork(IPAddress NetworkAddress, int PrefixLength)
    {
        public static bool TryParse(string? value, out IpNetwork network)
        {
            network = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var parts = value.Trim().Split('/', 2);
            if (!IPAddress.TryParse(parts[0], out var address))
            {
                return false;
            }
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            var maximum = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var prefix = parts.Length == 1
                ? maximum
                : int.TryParse(parts[1], out var parsed) ? parsed : -1;
            if (prefix is < 0 || prefix > maximum)
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            ApplyMask(bytes, prefix);
            network = new IpNetwork(new IPAddress(bytes), prefix);
            return true;
        }

        public bool Contains(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }
            if (address.AddressFamily != NetworkAddress.AddressFamily)
            {
                return false;
            }

            var candidate = address.GetAddressBytes();
            ApplyMask(candidate, PrefixLength);
            return candidate.SequenceEqual(NetworkAddress.GetAddressBytes());
        }

        public override string ToString() => $"{NetworkAddress}/{PrefixLength}";

        private static void ApplyMask(byte[] bytes, int prefixLength)
        {
            for (var bit = prefixLength; bit < bytes.Length * 8; bit++)
            {
                bytes[bit / 8] &= (byte)~(1 << (7 - bit % 8));
            }
        }
    }
}

public sealed class EndpointPolicyException(string message) : InvalidOperationException(message);
