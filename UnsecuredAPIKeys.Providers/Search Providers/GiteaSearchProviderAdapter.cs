using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Providers.Search_Providers;

/// <summary>
/// Versioned Gitea adapter (Phase 5, Task 22.2). Admin-approved self-hosted only.
/// Flavor/version detection runs before discovery/credential validation: only
/// flavor/version-validated capabilities are exposed. Public search stays disabled
/// (no GlobalPublicSearch declared) unless a validated server + consent exists.
/// Endpoint Policy must pass complete self-hosted safety before any traffic.
/// </summary>
public sealed class GiteaSearchProviderAdapter : ISearchProviderAdapter
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _directClient;
    private readonly ISearchProviderOutcomeClassifier _classifier;

    public SearchProviderEnum ProviderKind => SearchProviderEnum.Gitea;
    public string AdapterVersion => "gitea-search-v1";
    public SearchProviderCapability DeclaredCapabilities =>
        SearchProviderCapability.CodeSearch |
        SearchProviderCapability.PaginatedSearch |
        SearchProviderCapability.ContentRetrieval |
        SearchProviderCapability.PrivateRepositories |
        SearchProviderCapability.SelfHosted |
        SearchProviderCapability.RepositoryMetadata |
        SearchProviderCapability.NativeQueryOverrides;
    public ProviderSettingsSchema SettingsSchema => new(1,
        [
            new ProviderSettingDefinition("ApiFlavor", "string", true, "Must be 'gitea' (non-secret)."),
            new ProviderSettingDefinition("ServerVersion", "string", true, "Gitea server version, e.g. '1.24.0' (non-secret).")
        ]);

    public GiteaSearchProviderAdapter() : this(httpClientFactory: null, classifier: null) { }
    public GiteaSearchProviderAdapter(HttpClient httpClient, ISearchProviderOutcomeClassifier? classifier = null)
    {
        _directClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _classifier = classifier ?? new GiteaResponseClassifier();
    }
    [ActivatorUtilitiesConstructor]
    public GiteaSearchProviderAdapter(IHttpClientFactory? httpClientFactory, ISearchProviderOutcomeClassifier? classifier = null)
    {
        _httpClientFactory = httpClientFactory;
        _classifier = classifier ?? new GiteaResponseClassifier();
    }

    internal static bool IsSupportedVersion(string settingsJson, out string? sanitizedFlavor, out string? sanitizedVersion)
    {
        sanitizedFlavor = null;
        sanitizedVersion = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson);
            if (doc.RootElement.TryGetProperty("ApiFlavor", out var f)) sanitizedFlavor = f.GetString();
            else if (doc.RootElement.TryGetProperty("apiFlavor", out var f2)) sanitizedFlavor = f2.GetString();
            else if (doc.RootElement.TryGetProperty("flavor", out var f3)) sanitizedFlavor = f3.GetString();
            if (doc.RootElement.TryGetProperty("ServerVersion", out var v)) sanitizedVersion = v.GetString();
            else if (doc.RootElement.TryGetProperty("serverVersion", out var v2)) sanitizedVersion = v2.GetString();
            else if (doc.RootElement.TryGetProperty("version", out var v3)) sanitizedVersion = v3.GetString();
        }
        catch { return false; }

        if (!string.Equals(sanitizedFlavor, "gitea", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(sanitizedVersion)) return false;
        // Supported: Gitea >= 1.20 (design ref 1.24). Reject older/unknown.
        var digits = new string(sanitizedVersion.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        var parts = digits.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var major)) return false;
        if (major < 1) return false;
        if (major == 1)
        {
            if (parts.Length < 2 || !int.TryParse(parts[1], out var minor)) return false;
            if (minor < 20) return false;
        }
        return true;
    }

    private HttpClient CreateClient(ValidatedProviderInstance instance)
    {
        if (_directClient is not null) return _directClient;
        var client = _httpClientFactory?.CreateClient("GiteaSearch") ?? new HttpClient();
        var baseUri = $"{instance.Scheme}://{instance.Host}:{instance.Port}{instance.BasePath.TrimEnd('/')}/";
        if (client.BaseAddress is null) client.BaseAddress = new Uri(baseUri);
        return client;
    }

    private static void ApplyCredentialHeaders(HttpRequestMessage request, CredentialMaterial? credential)
    {
        if (credential is null || string.IsNullOrWhiteSpace(credential.Value)) return;
        request.Headers.Authorization = new AuthenticationHeaderValue("token", credential.Value);
    }

    public Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(ValidatedProviderInstance instance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.ProviderKind != SearchProviderEnum.Gitea)
            return Task.FromResult(new ProviderCapabilityResult(ProviderOutcomeKind.RequestInvalid, SearchProviderCapability.None, SanitizedCode: "KindMismatch"));
        if (!IsSupportedVersion(instance.SettingsJson, out _, out _))
            return Task.FromResult(new ProviderCapabilityResult(ProviderOutcomeKind.RequestInvalid, SearchProviderCapability.None, SanitizedCode: "UnsupportedVersion"));
        return Task.FromResult(new ProviderCapabilityResult(ProviderOutcomeKind.Success, DeclaredCapabilities));
    }

    public async Task<ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>> ValidateCredentialAsync(ProviderOperationContext context, CredentialMaterial credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(credential);
        ValidatedOperationRuntime.RequireSlotAndLease(context);
        if (!IsSupportedVersion(context.ProviderInstance.SettingsJson, out _, out _))
            return new ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>(ProviderOutcomeKind.RequestInvalid, new CredentialValidationResult(false), SanitizedCode: "UnsupportedVersion");
        var client = CreateClient(context.ProviderInstance);
        var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/user");
        ApplyCredentialHeaders(request, credential);
        request.Headers.UserAgent.ParseAdd("UnsecuredAPIKeys-Platform/1.0");
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            string? prefix = null;
            if (!response.IsSuccessStatusCode) prefix = await ReadBoundedPrefixAsync(response.Content, 512, cancellationToken);
            var outcome = _classifier.Classify(ProviderKind, response.StatusCode, null, prefix);
            return new ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>(outcome, new CredentialValidationResult(outcome == ProviderOutcomeKind.Success), SanitizedCode: outcome.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var outcome = _classifier.Classify(ProviderKind, null, ex);
            return new ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>(outcome, new CredentialValidationResult(false), SanitizedCode: outcome.ToString());
        }
    }

    public Task<TranslatedProviderQuery> TranslateQueryAsync(ProviderOperationContext context, SearchQuerySnapshot query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var effective = !string.IsNullOrWhiteSpace(query.NativeOverride) ? query.NativeOverride : query.GenericQuery;
        return Task.FromResult(new TranslatedProviderQuery(effective, query.SettingsJson));
    }

    public async IAsyncEnumerable<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>> SearchAsync(
        ProviderOperationContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Credential is null) ValidatedOperationRuntime.RequireSlot(context);
        else ValidatedOperationRuntime.RequireSlotAndLease(context);
        if (context.ContinuationAdapterVersion != null && !string.Equals(context.ContinuationAdapterVersion, AdapterVersion, StringComparison.Ordinal))
        {
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(ProviderOutcomeKind.RequestInvalid, new NormalizedSearchPage([]), context.Continuation, SanitizedCode: "IncompatibleContinuation");
            yield break;
        }
        if (!IsSupportedVersion(context.ProviderInstance.SettingsJson, out _, out _))
        {
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(ProviderOutcomeKind.RequestInvalid, new NormalizedSearchPage([]), context.Continuation, SanitizedCode: "UnsupportedVersion");
            yield break;
        }
        var page = 1;
        var effectiveQuery = context.SearchQuery ?? string.Empty;
        var malformed = false;
        if (!string.IsNullOrWhiteSpace(context.Continuation))
        {
            try
            {
                using var doc = JsonDocument.Parse(context.Continuation);
                if (doc.RootElement.TryGetProperty("page", out var p) && p.TryGetInt32(out var pp)) page = Math.Max(1, pp);
                else if (doc.RootElement.TryGetProperty("offset", out var o) && o.TryGetInt32(out var po)) page = Math.Max(1, (po / 100) + 1);
                if (doc.RootElement.TryGetProperty("query", out var q)) { var cq = q.GetString(); if (!string.IsNullOrWhiteSpace(cq)) effectiveQuery = cq; }
            }
            catch { malformed = true; }
        }
        if (malformed)
        {
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(ProviderOutcomeKind.RequestInvalid, new NormalizedSearchPage([]), context.Continuation, SanitizedCode: "MalformedContinuation");
            yield break;
        }

        var client = CreateClient(context.ProviderInstance);
        const int limit = 100;
        var requestUri = $"api/v1/search/code?q={Uri.EscapeDataString(effectiveQuery)}&page={page}&limit={limit}";
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        ApplyCredentialHeaders(request, context.Credential);
        request.Headers.UserAgent.ParseAdd("UnsecuredAPIKeys-Platform/1.0");
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage? response = null;
        ProviderOutcomeKind? transportOutcome = null;
        try { response = await client.SendAsync(request, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        { transportOutcome = _classifier.Classify(ProviderKind, null, ex); }
        if (transportOutcome.HasValue)
        {
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(transportOutcome.Value, new NormalizedSearchPage([]), context.Continuation, SanitizedCode: transportOutcome.Value.ToString());
            yield break;
        }
        if (response is null)
        {
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(ProviderOutcomeKind.Transient, new NormalizedSearchPage([]), context.Continuation, SanitizedCode: "NullResponse");
            yield break;
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var prefix = await ReadBoundedPrefixAsync(response.Content, 512, cancellationToken);
                var outcome = _classifier.Classify(ProviderKind, response.StatusCode, null, prefix);
                yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(outcome, new NormalizedSearchPage([]), context.Continuation, SanitizedCode: outcome.ToString());
                yield break;
            }
            var results = new List<object>();
            using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var jsonDoc = await JsonDocument.ParseAsync(bodyStream, default, cancellationToken);
            var root = jsonDoc.RootElement;
            IEnumerable<JsonElement> arr;
            if (root.ValueKind == JsonValueKind.Array)
            {
                arr = root.EnumerateArray().ToArray();
            }
            else if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
            {
                arr = d.EnumerateArray().ToArray();
            }
            else
            {
                arr = Array.Empty<JsonElement>();
            }
            foreach (var item in arr)
            {
                var repoFull = item.TryGetProperty("repo", out var rp) ? (rp.TryGetProperty("full_name", out var fn) ? fn.GetString() ?? string.Empty : string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(repoFull) && item.TryGetProperty("repository", out var rp2)) repoFull = rp2.GetString() ?? string.Empty;
                var path = item.TryGetProperty("path", out var pp) ? pp.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(repoFull) || string.IsNullOrWhiteSpace(path)) continue;
                var sha = item.TryGetProperty("sha", out var sp) ? sp.GetString() : null;
                var safeRev = !string.IsNullOrWhiteSpace(sha) ? sha! : "HEAD";
                var slash = repoFull.IndexOf('/');
                var baseUri = $"{context.ProviderInstance.Scheme}://{context.ProviderInstance.Host}:{context.ProviderInstance.Port}{context.ProviderInstance.BasePath.TrimEnd('/')}";
                var provenance = $"{baseUri}/api/v1/repos/{repoFull}/raw/{Uri.EscapeDataString(path)}?ref={Uri.EscapeDataString(safeRev)}";
                results.Add(new ProviderResultInput(
                    ProviderKind: SearchProviderEnum.Gitea,
                    ProviderInstanceStableId: context.ProviderInstance.StableId,
                    RepositoryStableId: repoFull,
                    ImmutableRevisionOrEquivalentVersion: safeRev,
                    NormalizedFilePath: path,
                    RepositoryOwner: slash > 0 ? repoFull[..slash] : repoFull,
                    RepositoryName: slash > 0 ? repoFull[(slash + 1)..] : repoFull,
                    FileName: Path.GetFileName(path),
                    Snippet: item.TryGetProperty("content", out var cp) ? cp.GetString() : null,
                    ProvenanceUrl: provenance,
                    Branch: safeRev));
                if (results.Count >= context.Bounds.MaxEventCount) break;
            }
            string? next = results.Count >= limit ? JsonSerializer.Serialize(new { page = page + 1, query = effectiveQuery }) : null;
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(ProviderOutcomeKind.Success, new NormalizedSearchPage(results), next);
        }
    }

    public async Task<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>> FetchContentAsync(ProviderOperationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Credential is null) ValidatedOperationRuntime.RequireSlot(context);
        else ValidatedOperationRuntime.RequireSlotAndLease(context);
        if (!IsSupportedVersion(context.ProviderInstance.SettingsJson, out _, out _))
            return new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(ProviderOutcomeKind.RequestInvalid, null, SanitizedCode: "UnsupportedVersion");
        var requestUrl = ResolveContentUrl(context);
        if (requestUrl is null) return new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(ProviderOutcomeKind.RequestInvalid, null, SanitizedCode: "MissingContentLocation");
        var client = CreateClient(context.ProviderInstance);
        var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        ApplyCredentialHeaders(request, context.Credential);
        request.Headers.UserAgent.ParseAdd("UnsecuredAPIKeys-Platform/1.0");
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var prefix = await ReadBoundedPrefixAsync(response.Content, 512, cancellationToken);
                var outcome = _classifier.Classify(ProviderKind, response.StatusCode, null, prefix);
                return new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(outcome, null, SanitizedCode: outcome.ToString());
            }
            var rawBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var rawContent = Encoding.UTF8.GetString(rawBytes);
            var version = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
            return new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(ProviderOutcomeKind.Success, new NormalizedContent(rawContent, version));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var outcome = _classifier.Classify(ProviderKind, null, ex);
            return new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(outcome, null, SanitizedCode: outcome.ToString());
        }
    }

    private static string? ResolveContentUrl(ProviderOperationContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ContentApiUrl)) return context.ContentApiUrl;
        if (string.IsNullOrWhiteSpace(context.ContentPath) || string.IsNullOrWhiteSpace(context.ContentRepositoryOwner) || string.IsNullOrWhiteSpace(context.ContentRepositoryName)) return null;
        var safeRev = !string.IsNullOrWhiteSpace(context.ContentRevision) ? context.ContentRevision! : "HEAD";
        var baseUri = $"{context.ProviderInstance.Scheme}://{context.ProviderInstance.Host}:{context.ProviderInstance.Port}{context.ProviderInstance.BasePath.TrimEnd('/')}";
        return $"{baseUri}/api/v1/repos/{context.ContentRepositoryOwner}/{context.ContentRepositoryName}/raw/{context.ContentPath.TrimStart('/')}?ref={Uri.EscapeDataString(safeRev)}";
    }

    private static async Task<string?> ReadBoundedPrefixAsync(HttpContent? content, int maxChars, CancellationToken ct)
    {
        if (content is null) return null;
        try
        {
            using var stream = await content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[maxChars];
            var read = await reader.ReadBlockAsync(buffer, 0, maxChars);
            return read > 0 ? new string(buffer, 0, read) : null;
        }
        catch { return null; }
    }
}
