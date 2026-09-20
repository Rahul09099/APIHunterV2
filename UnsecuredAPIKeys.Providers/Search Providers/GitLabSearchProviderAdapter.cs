using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Providers.Search_Providers;

/// <summary>
/// Versioned GitLab Search Provider Adapter adhering to the closed platform contract (Wave 11, Task 11.2).
/// Supports SaaS (gitlab.com/api/v4) and self-hosted instances through Endpoint-Policy-validated
/// configuration. Gated by Operation Slots and credential Claim Leases. Never swallows errors.
/// Redacts raw error bodies to bounded allowlisted evidence via <see cref="GitLabResponseClassifier"/>.
/// </summary>
public sealed class GitLabSearchProviderAdapter : ISearchProviderAdapter, ISearchProvider
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _directClient;
    private readonly ISearchProviderOutcomeClassifier _classifier;
    private readonly GitLabSearchProvider _legacyProvider;

    public SearchProviderEnum ProviderKind => SearchProviderEnum.GitLab;

    public string ProviderName => _legacyProvider.ProviderName;

    public string AdapterVersion => "gitlab-metadata-v1";

    public SearchProviderCapability DeclaredCapabilities =>
        SearchProviderCapability.CodeSearch |
        SearchProviderCapability.PaginatedSearch |
        SearchProviderCapability.ContentRetrieval |
        SearchProviderCapability.PrivateRepositories |
        SearchProviderCapability.GlobalPublicSearch |
        SearchProviderCapability.SelfHosted |
        SearchProviderCapability.RepositoryMetadata |
        SearchProviderCapability.NativeQueryOverrides;

    public ProviderSettingsSchema SettingsSchema => new(
        1,
        [
            new ProviderSettingDefinition(
                "ApiFlavor",
                "string",
                false,
                "Optional non-secret GitLab API flavor selector.")
        ]);

    public GitLabSearchProviderAdapter()
        : this(httpClientFactory: null, classifier: null, legacyProvider: null)
    {
    }

    public GitLabSearchProviderAdapter(GitLabSearchProvider legacyProvider)
        : this(httpClientFactory: null, classifier: null, legacyProvider: legacyProvider)
    {
    }

    public GitLabSearchProviderAdapter(HttpClient httpClient, ISearchProviderOutcomeClassifier? classifier = null)
    {
        _directClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _classifier = classifier ?? new GitLabResponseClassifier();
        _legacyProvider = new GitLabSearchProvider();
    }

    [ActivatorUtilitiesConstructor]
    public GitLabSearchProviderAdapter(
        IHttpClientFactory? httpClientFactory,
        ISearchProviderOutcomeClassifier? classifier = null,
        GitLabSearchProvider? legacyProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _classifier = classifier ?? new GitLabResponseClassifier();
        _legacyProvider = legacyProvider ?? new GitLabSearchProvider();
    }

    private HttpClient CreateClient(ValidatedProviderInstance instance)
    {
        if (_directClient is not null)
        {
            return _directClient;
        }

        var client = _httpClientFactory?.CreateClient("GitLabSearch") ?? new HttpClient();
        var baseUri = $"{instance.Scheme}://{instance.Host}:{instance.Port}{instance.BasePath.TrimEnd('/')}/";
        if (client.BaseAddress is null)
        {
            client.BaseAddress = new Uri(baseUri);
        }
        return client;
    }

    private static void ApplyCredentialHeaders(HttpRequestMessage request, CredentialMaterial? credential)
    {
        if (credential is null || string.IsNullOrWhiteSpace(credential.Value))
        {
            return;
        }

        // GitLab accepts the PAT via PRIVATE-TOKEN (primary) or Bearer (OAuth).
        // Sending both keeps SaaS and self-hosted behavior identical.
        request.Headers.Add("PRIVATE-TOKEN", credential.Value);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value);
    }

    // ── 1. DiscoverCapabilitiesAsync ─────────────────────────────────────────────

    public Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(
        ValidatedProviderInstance instance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.ProviderKind != SearchProviderEnum.GitLab)
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
        ApplyCredentialHeaders(request, credential);
        request.Headers.UserAgent.ParseAdd("UnsecuredAPIKeys-Platform/1.0");
        request.Headers.Accept.ParseAdd("application/json");

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
    //
    // GET {base}/search?scope=blobs&search={q}&page={page}&per_page=100
    // Pagination is header-driven (X-Next-Page / X-Total-Pages); the versioned
    // Continuation is JSON {page, query} bound to AdapterVersion.

    public async IAsyncEnumerable<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>> SearchAsync(
        ProviderOperationContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
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
        var requestUri = $"search?scope=blobs&search={Uri.EscapeDataString(effectiveQuery)}&page={page}&per_page={perPage}";

        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        ApplyCredentialHeaders(request, context.Credential);
        request.Headers.UserAgent.ParseAdd("UnsecuredAPIKeys-Platform/1.0");
        request.Headers.Accept.ParseAdd("application/json");

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

            using (var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var jsonDoc = await JsonDocument.ParseAsync(bodyStream, default, cancellationToken))
            {
                if (jsonDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in jsonDoc.RootElement.EnumerateArray())
                    {
                        var projectId = item.TryGetProperty("project_id", out var idProp)
                            ? idProp.ToString()
                            : string.Empty;
                        var path = item.TryGetProperty("path", out var pathProp)
                            ? pathProp.GetString() ?? string.Empty
                            : string.Empty;
                        var fileName = item.TryGetProperty("filename", out var fnProp)
                            ? fnProp.GetString()
                            : (!string.IsNullOrEmpty(path) ? Path.GetFileName(path) : null);
                        var blobRef = item.TryGetProperty("ref", out var refProp)
                            ? refProp.GetString()
                            : null;
                        var safeRef = !string.IsNullOrWhiteSpace(blobRef) ? blobRef : "HEAD";
                        var snippet = item.TryGetProperty("data", out var dataProp)
                            ? dataProp.GetString()
                            : null;
                        var lineNumber = item.TryGetProperty("startline", out var lineProp) &&
                            lineProp.ValueKind == JsonValueKind.Number &&
                            lineProp.TryGetInt32(out var parsedLine)
                            ? parsedLine
                            : (int?)null;

                        var encodedPath = Uri.EscapeDataString(path);
                        var baseUri = $"{context.ProviderInstance.Scheme}://{context.ProviderInstance.Host}:{context.ProviderInstance.Port}{context.ProviderInstance.BasePath.TrimEnd('/')}";
                        var rawUrl = $"{baseUri}/projects/{projectId}/repository/files/{encodedPath}/raw?ref={Uri.EscapeDataString(safeRef)}";

                        // Search-stage revision identity: GitLab blob search returns no
                        // immutable blob SHA, only the (mutable) ref. The authoritative
                        // content version is derived at fetch time via SHA-256 over the
                        // raw bytes (AC-10.24); the ref is recorded as branch context.
                        var resultInput = new ProviderResultInput(
                            ProviderKind: SearchProviderEnum.GitLab,
                            ProviderInstanceStableId: context.ProviderInstance.StableId,
                            RepositoryStableId: projectId,
                            ImmutableRevisionOrEquivalentVersion: safeRef,
                            NormalizedFilePath: path,
                            RepositoryOwner: $"gitlab-project-{projectId}",
                            RepositoryName: $"project-{projectId}",
                            FileName: fileName,
                            LineNumber: lineNumber,
                            Snippet: snippet,
                            ProvenanceUrl: rawUrl,
                            Branch: safeRef);

                        results.Add(resultInput);
                    }
                }
            }

            string? nextContinuation = null;
            if (TryGetNextPage(response, page, out var nextPage))
            {
                nextContinuation = JsonSerializer.Serialize(new { page = nextPage, query = effectiveQuery });
            }

            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success,
                new NormalizedSearchPage(results),
                nextContinuation);
        }
    }

    // ── 5. FetchContentAsync ─────────────────────────────────────────────────────
    //
    // GET {base}/projects/{id}/repository/files/{encodedPath}/raw?ref={ref}
    // Callers should pass the raw API URL via ContentApiUrl (as produced by
    // SearchAsync provenance). A numeric project id carried in
    // ContentRepositoryName (or Owner) with ContentPath is also accepted.

    public async Task<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>> FetchContentAsync(
        ProviderOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Credential is null)
            ValidatedOperationRuntime.RequireSlot(context);
        else
            ValidatedOperationRuntime.RequireSlotAndLease(context);

        var requestUrl = ResolveContentUrl(context);
        if (requestUrl is null)
        {
            return new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(
                ProviderOutcomeKind.RequestInvalid,
                null,
                SanitizedCode: "MissingContentLocation");
        }

        var client = CreateClient(context.ProviderInstance);
        var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        ApplyCredentialHeaders(request, context.Credential);
        request.Headers.UserAgent.ParseAdd("UnsecuredAPIKeys-Platform/1.0");
        request.Headers.Accept.ParseAdd("application/json");

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

            var rawBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var rawContent = Encoding.UTF8.GetString(rawBytes);

            // AC-10.24: deterministic content version — SHA-256 hex over the raw
            // content bytes. Branch names and URLs are never version inputs.
            var contentVersion = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();

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

    private static string? ResolveContentUrl(ProviderOperationContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ContentApiUrl))
        {
            return context.ContentApiUrl;
        }

        // Accept a numeric GitLab project id in either repository field.
        var projectId = FirstNumericId(context.ContentRepositoryName, context.ContentRepositoryOwner);
        if (projectId is null || string.IsNullOrWhiteSpace(context.ContentPath))
        {
            return null;
        }

        var safeRef = !string.IsNullOrWhiteSpace(context.ContentRevision)
            ? context.ContentRevision
            : "HEAD";
        var baseUri = $"{context.ProviderInstance.Scheme}://{context.ProviderInstance.Host}:{context.ProviderInstance.Port}{context.ProviderInstance.BasePath.TrimEnd('/')}";
        var encodedPath = Uri.EscapeDataString(context.ContentPath.TrimStart('/'));
        return $"{baseUri}/projects/{projectId}/repository/files/{encodedPath}/raw?ref={Uri.EscapeDataString(safeRef)}";
    }

    private static string? FirstNumericId(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                long.TryParse(candidate.Trim(), out _))
            {
                return candidate.Trim();
            }
        }
        return null;
    }

    private static bool TryGetNextPage(HttpResponseMessage response, int currentPage, out int nextPage)
    {
        nextPage = currentPage + 1;

        // X-Next-Page is empty on the last page.
        if (response.Headers.TryGetValues("X-Next-Page", out var nextValues))
        {
            var rawNext = nextValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(rawNext) &&
                int.TryParse(rawNext, out var parsed) &&
                parsed > currentPage)
            {
                nextPage = parsed;
                return true;
            }
            return false;
        }

        // Fall back to X-Total-Pages when X-Next-Page is absent.
        if (response.Headers.TryGetValues("X-Total-Pages", out var totalValues))
        {
            var rawTotal = totalValues.FirstOrDefault();
            if (int.TryParse(rawTotal, out var totalPages) && currentPage < totalPages)
            {
                return true;
            }
            return false;
        }

        return false;
    }

    private static async Task<string?> ReadBoundedPrefixAsync(HttpContent? content, int maxChars, CancellationToken ct)
    {
        if (content is null)
        {
            return null;
        }

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
