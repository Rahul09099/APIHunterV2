using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Providers.Search_Providers;

/// <summary>
/// Versioned GitHub Search Provider Adapter adhering to the closed platform contract (Wave 10, Task 10.3).
/// Gated by Operation Slots and credential Claim Leases. Never swallows errors. Redacts raw error bodies.
/// </summary>
public sealed class GitHubSearchProviderAdapter : ISearchProviderAdapter, ISearchProvider
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _directClient;
    private readonly ISearchProviderOutcomeClassifier _classifier;
    private readonly GitHubSearchProvider _legacyProvider;

    public SearchProviderEnum ProviderKind => SearchProviderEnum.GitHub;

    public string ProviderName => _legacyProvider.ProviderName;

    public string AdapterVersion => "github-metadata-v1";

    public SearchProviderCapability DeclaredCapabilities =>
        SearchProviderCapability.CodeSearch |
        SearchProviderCapability.PaginatedSearch |
        SearchProviderCapability.ContentRetrieval |
        SearchProviderCapability.PrivateRepositories |
        SearchProviderCapability.GlobalPublicSearch |
        SearchProviderCapability.RepositoryMetadata |
        SearchProviderCapability.NativeQueryOverrides;

    public ProviderSettingsSchema SettingsSchema => ProviderSettingsSchema.Empty;

    public GitHubSearchProviderAdapter()
        : this(httpClientFactory: null, classifier: null, legacyProvider: null)
    {
    }

    public GitHubSearchProviderAdapter(GitHubSearchProvider legacyProvider)
        : this(httpClientFactory: null, classifier: null, legacyProvider: legacyProvider)
    {
    }

    public GitHubSearchProviderAdapter(HttpClient httpClient, ISearchProviderOutcomeClassifier? classifier = null)
    {
        _directClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _classifier = classifier ?? new GitHubResponseClassifier();
        _legacyProvider = new GitHubSearchProvider();
    }

    [ActivatorUtilitiesConstructor]
    public GitHubSearchProviderAdapter(
        IHttpClientFactory? httpClientFactory,
        ISearchProviderOutcomeClassifier? classifier = null,
        GitHubSearchProvider? legacyProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _classifier = classifier ?? new GitHubResponseClassifier();
        _legacyProvider = legacyProvider ?? new GitHubSearchProvider();
    }

    private HttpClient CreateClient(ValidatedProviderInstance instance)
    {
        if (_directClient is not null)
        {
            return _directClient;
        }

        var client = _httpClientFactory?.CreateClient("GitHubSearch") ?? new HttpClient();
        var baseUri = $"{instance.Scheme}://{instance.Host}:{instance.Port}{instance.BasePath.TrimEnd('/')}/";
        if (client.BaseAddress is null)
        {
            client.BaseAddress = new Uri(baseUri);
        }
        return client;
    }

    // ── 1. DiscoverCapabilitiesAsync ─────────────────────────────────────────────

    public Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(
        ValidatedProviderInstance instance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.ProviderKind != SearchProviderEnum.GitHub)
        {
            return Task.FromResult(new ProviderCapabilityResult(
                ProviderOutcomeKind.RequestInvalid,
                SearchProviderCapability.None,
                SanitizedCode: "KindMismatch"));
        }

        return Task.FromResult(new ProviderCapabilityResult(
            ProviderOutcomeKind.Success,
            DeclaredCapabilities));
    }

    // ── 2. ValidateCredentialAsync ───────────────────────────────────────────────

    public async Task<ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>> ValidateCredentialAsync(
        ProviderOperationContext context,
        CredentialMaterial credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(credential);

        ValidatedOperationRuntime.RequireSlotAndLease(context);

        var client = CreateClient(context.ProviderInstance);
        var request = new HttpRequestMessage(HttpMethod.Get, "user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value);
        request.Headers.UserAgent.ParseAdd("UnsecuredAPIKeys-Platform/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            string? prefix = null;
            if (!response.IsSuccessStatusCode)
            {
                prefix = await ReadBoundedPrefixAsync(response.Content, 512, cancellationToken);
            }

            var outcome = _classifier.Classify(
                ProviderKind,
                response.StatusCode,
                exception: null,
                responseBody: prefix);

            var isValid = outcome == ProviderOutcomeKind.Success;
            return new ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>(
                outcome,
                new CredentialValidationResult(isValid),
                SanitizedCode: outcome.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var outcome = _classifier.Classify(ProviderKind, null, ex);
            return new ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>(
                outcome,
                new CredentialValidationResult(false),
                SanitizedCode: outcome.ToString());
        }
    }

    // ── 3. TranslateQueryAsync ───────────────────────────────────────────────────

    public Task<TranslatedProviderQuery> TranslateQueryAsync(
        ProviderOperationContext context,
        SearchQuerySnapshot query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);

        var effectiveQuery = !string.IsNullOrWhiteSpace(query.NativeOverride)
            ? query.NativeOverride
            : query.GenericQuery;

        return Task.FromResult(new TranslatedProviderQuery(effectiveQuery, query.SettingsJson));
    }

    // ── 4. SearchAsync (Versioned) ───────────────────────────────────────────────

    public async IAsyncEnumerable<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>> SearchAsync(
        ProviderOperationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Public operations carry a Slot but no credential or Lease; credentialed
        // operations require both. Credential presence distinguishes the two (Task 14.2).
        if (context.Credential is null)
            ValidatedOperationRuntime.RequireSlot(context);
        else
            ValidatedOperationRuntime.RequireSlotAndLease(context);

        if (context.ContinuationAdapterVersion != null &&
            !string.Equals(context.ContinuationAdapterVersion, AdapterVersion, StringComparison.Ordinal))
        {
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                ProviderOutcomeKind.RequestInvalid,
                new NormalizedSearchPage([]),
                context.Continuation,
                SanitizedCode: "IncompatibleContinuation");
            yield break;
        }

        var page = 1;
        var effectiveQuery = context.SearchQuery ?? string.Empty;

        var malformedContinuation = false;
        if (!string.IsNullOrWhiteSpace(context.Continuation))
        {
            try
            {
                using var doc = JsonDocument.Parse(context.Continuation);
                if (doc.RootElement.TryGetProperty("page", out var pageProp) && pageProp.TryGetInt32(out var parsedPage))
                {
                    page = parsedPage;
                }
                if (doc.RootElement.TryGetProperty("query", out var queryProp))
                {
                    var continuationQuery = queryProp.GetString();
                    if (!string.IsNullOrWhiteSpace(continuationQuery))
                    {
                        effectiveQuery = continuationQuery;
                    }
                }
            }
            catch
            {
                malformedContinuation = true;
            }
        }

        if (malformedContinuation)
        {
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                ProviderOutcomeKind.RequestInvalid,
                new NormalizedSearchPage([]),
                context.Continuation,
                SanitizedCode: "MalformedContinuation");
            yield break;
        }

        var client = CreateClient(context.ProviderInstance);
        const int perPage = 100;
        var requestUri = $"search/code?q={Uri.EscapeDataString(effectiveQuery)}&page={page}&per_page={perPage}&sort=indexed&order=desc";

        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (context.Credential != null && !string.IsNullOrWhiteSpace(context.Credential.Value))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Credential.Value);
        }
        request.Headers.UserAgent.ParseAdd("UnsecuredAPIKeys-Platform/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github.v3.text-match+json");

        HttpResponseMessage? response = null;
        ProviderOutcomeKind? transportOutcome = null;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            transportOutcome = _classifier.Classify(ProviderKind, null, ex);
        }

        if (transportOutcome.HasValue)
        {
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                transportOutcome.Value,
                new NormalizedSearchPage([]),
                context.Continuation,
                SanitizedCode: transportOutcome.Value.ToString());
            yield break;
        }

        if (response is null)
        {
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                ProviderOutcomeKind.Transient,
                new NormalizedSearchPage([]),
                context.Continuation,
                SanitizedCode: "NullResponse");
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var prefix = await ReadBoundedPrefixAsync(response.Content, 512, cancellationToken);
                var outcome = _classifier.Classify(
                    ProviderKind,
                    response.StatusCode,
                    exception: null,
                    responseBody: prefix);

                yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                    outcome,
                    new NormalizedSearchPage([]),
                    context.Continuation,
                    SanitizedCode: outcome.ToString());
                yield break;
            }

            var results = new List<object>();
            int totalCount = 0;

            using (var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var jsonDoc = await JsonDocument.ParseAsync(bodyStream, default, cancellationToken))
            {
                if (jsonDoc.RootElement.TryGetProperty("total_count", out var totalProp))
                {
                    totalCount = totalProp.GetInt32();
                }

                if (jsonDoc.RootElement.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in itemsProp.EnumerateArray())
                    {
                        var repoStableId = string.Empty;
                        string? repoOwner = null;
                        string? repoName = null;

                        if (item.TryGetProperty("repository", out var repoProp))
                        {
                            if (repoProp.TryGetProperty("id", out var idProp))
                            {
                                repoStableId = idProp.ToString();
                            }
                            if (repoProp.TryGetProperty("name", out var nameProp))
                            {
                                repoName = nameProp.GetString();
                            }
                            if (repoProp.TryGetProperty("owner", out var ownerProp) &&
                                ownerProp.TryGetProperty("login", out var loginProp))
                            {
                                repoOwner = loginProp.GetString();
                            }
                        }

                        var path = item.TryGetProperty("path", out var pathProp) ? pathProp.GetString() ?? string.Empty : string.Empty;
                        var fileName = item.TryGetProperty("name", out var fnProp) ? fnProp.GetString() : null;
                        var sha = item.TryGetProperty("sha", out var shaProp) ? shaProp.GetString() ?? string.Empty : string.Empty;
                        var htmlUrl = item.TryGetProperty("html_url", out var htmlUrlProp) ? htmlUrlProp.GetString() : null;

                        string? snippet = null;
                        if (item.TryGetProperty("text_matches", out var tmProp) && tmProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tm in tmProp.EnumerateArray())
                            {
                                if (tm.TryGetProperty("fragment", out var fragProp))
                                {
                                    snippet = fragProp.GetString();
                                    break;
                                }
                            }
                        }

                        var resultInput = new ProviderResultInput(
                            ProviderKind: SearchProviderEnum.GitHub,
                            ProviderInstanceStableId: context.ProviderInstance.StableId,
                            RepositoryStableId: repoStableId,
                            ImmutableRevisionOrEquivalentVersion: sha,
                            NormalizedFilePath: path,
                            RepositoryOwner: repoOwner,
                            RepositoryName: repoName,
                            FileName: fileName,
                            Snippet: snippet,
                            ProvenanceUrl: htmlUrl);

                        results.Add(resultInput);
                    }
                }
            }

            string? nextContinuation = null;
            if (page * perPage < totalCount)
            {
                nextContinuation = JsonSerializer.Serialize(new { page = page + 1, query = effectiveQuery });
            }

            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success,
                new NormalizedSearchPage(results),
                nextContinuation);
        }
    }

    // ── 5. FetchContentAsync ─────────────────────────────────────────────────────

    public async Task<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>> FetchContentAsync(
        ProviderOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Credential is null)
            ValidatedOperationRuntime.RequireSlot(context);
        else
            ValidatedOperationRuntime.RequireSlotAndLease(context);

        var client = CreateClient(context.ProviderInstance);
        string requestUrl;

        if (!string.IsNullOrWhiteSpace(context.ContentApiUrl))
        {
            requestUrl = context.ContentApiUrl;
        }
        else if (!string.IsNullOrWhiteSpace(context.ContentRepositoryOwner) &&
                 !string.IsNullOrWhiteSpace(context.ContentRepositoryName) &&
                 !string.IsNullOrWhiteSpace(context.ContentPath))
        {
            var refParam = !string.IsNullOrWhiteSpace(context.ContentRevision)
                ? $"?ref={Uri.EscapeDataString(context.ContentRevision)}"
                : string.Empty;
            requestUrl = $"repos/{context.ContentRepositoryOwner}/{context.ContentRepositoryName}/contents/{context.ContentPath.TrimStart('/')}{refParam}";
        }
        else
        {
            return new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(
                ProviderOutcomeKind.RequestInvalid,
                null,
                SanitizedCode: "MissingContentLocation");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        if (context.Credential != null && !string.IsNullOrWhiteSpace(context.Credential.Value))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Credential.Value);
        }
        request.Headers.UserAgent.ParseAdd("UnsecuredAPIKeys-Platform/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github.v3+json");

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var prefix = await ReadBoundedPrefixAsync(response.Content, 512, cancellationToken);
                var outcome = _classifier.Classify(
                    ProviderKind,
                    response.StatusCode,
                    exception: null,
                    responseBody: prefix);

                return new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(
                    outcome,
                    null,
                    SanitizedCode: outcome.ToString());
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            string rawContent;
            string? blobSha = null;

            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                using var jsonDoc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    default,
                    cancellationToken);

                if (jsonDoc.RootElement.TryGetProperty("sha", out var shaProp))
                {
                    blobSha = shaProp.GetString();
                }

                if (jsonDoc.RootElement.TryGetProperty("content", out var contentProp) &&
                    jsonDoc.RootElement.TryGetProperty("encoding", out var encProp) &&
                    string.Equals(encProp.GetString(), "base64", StringComparison.OrdinalIgnoreCase))
                {
                    var base64 = contentProp.GetString() ?? string.Empty;
                    // GitHub base64 can contain newlines
                    var cleaned = base64.Replace("\n", "").Replace("\r", "");
                    var bytes = Convert.FromBase64String(cleaned);
                    rawContent = Encoding.UTF8.GetString(bytes);
                }
                else
                {
                    rawContent = await response.Content.ReadAsStringAsync(cancellationToken);
                }
            }
            else
            {
                rawContent = await response.Content.ReadAsStringAsync(cancellationToken);
            }

            // AC-10.24: Deterministic content version when blob SHA is not supplied
            var contentVersion = !string.IsNullOrWhiteSpace(blobSha)
                ? blobSha
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawContent))).ToLowerInvariant();

            return new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success,
                new NormalizedContent(rawContent, contentVersion));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var outcome = _classifier.Classify(ProviderKind, null, ex);
            return new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(
                outcome,
                null,
                SanitizedCode: outcome.ToString());
        }
    }

    // ── 6. Legacy ISearchProvider Bridge ────────────────────────────────────────

    public Task<SearchResponse> SearchAsync(
        SearchQuery query,
        SearchProviderToken? token,
        string? extraQueryParams,
        int startPage = 1) =>
        _legacyProvider.SearchAsync(query, token, extraQueryParams, startPage);

    private static async Task<string?> ReadBoundedPrefixAsync(HttpContent content, int maxChars, CancellationToken ct)
    {
        try
        {
            using var stream = await content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[maxChars];
            var read = await reader.ReadBlockAsync(buffer, 0, maxChars);
            return read > 0 ? new string(buffer, 0, read) : null;
        }
        catch
        {
            return null;
        }
    }
}
