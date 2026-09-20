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
/// Versioned Hugging Face Hub adapter (Phase 3, Task 20.2).
/// Uses only supported Hub APIs for authorized model/dataset/Space enumeration and
/// file content (GET /api/models, /api/datasets, /{repo}/raw/{rev}/{path}).
/// Never scrapes website pages or undocumented routes. Public operations require
/// capability + consent + Slot (dual gate); credentialed paths require Claim+Slot.
/// </summary>
public sealed class HuggingFaceSearchProviderAdapter : ISearchProviderAdapter
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _directClient;
    private readonly ISearchProviderOutcomeClassifier _classifier;

    public SearchProviderEnum ProviderKind => SearchProviderEnum.HuggingFace;

    public string AdapterVersion => "huggingface-hub-v1";

    public SearchProviderCapability DeclaredCapabilities =>
        SearchProviderCapability.CodeSearch |
        SearchProviderCapability.PaginatedSearch |
        SearchProviderCapability.ContentRetrieval |
        SearchProviderCapability.PrivateRepositories |
        SearchProviderCapability.GlobalPublicSearch |
        SearchProviderCapability.RepositoryMetadata |
        SearchProviderCapability.NativeQueryOverrides;

    public ProviderSettingsSchema SettingsSchema => ProviderSettingsSchema.Empty;

    public HuggingFaceSearchProviderAdapter()
        : this(httpClientFactory: null, classifier: null)
    {
    }

    public HuggingFaceSearchProviderAdapter(HttpClient httpClient, ISearchProviderOutcomeClassifier? classifier = null)
    {
        _directClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _classifier = classifier ?? new HuggingFaceResponseClassifier();
    }

    [ActivatorUtilitiesConstructor]
    public HuggingFaceSearchProviderAdapter(
        IHttpClientFactory? httpClientFactory,
        ISearchProviderOutcomeClassifier? classifier = null)
    {
        _httpClientFactory = httpClientFactory;
        _classifier = classifier ?? new HuggingFaceResponseClassifier();
    }

    private HttpClient CreateClient(ValidatedProviderInstance instance)
    {
        if (_directClient is not null) return _directClient;
        var client = _httpClientFactory?.CreateClient("HuggingFaceSearch") ?? new HttpClient();
        var baseUri = $"{instance.Scheme}://{instance.Host}:{instance.Port}{instance.BasePath.TrimEnd('/')}/";
        if (client.BaseAddress is null) client.BaseAddress = new Uri(baseUri);
        return client;
    }

    private static void ApplyCredentialHeaders(HttpRequestMessage request, CredentialMaterial? credential)
    {
        if (credential is null || string.IsNullOrWhiteSpace(credential.Value)) return;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value);
    }

    public Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(ValidatedProviderInstance instance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.ProviderKind != SearchProviderEnum.HuggingFace)
            return Task.FromResult(new ProviderCapabilityResult(ProviderOutcomeKind.RequestInvalid, SearchProviderCapability.None, SanitizedCode: "KindMismatch"));
        return Task.FromResult(new ProviderCapabilityResult(ProviderOutcomeKind.Success, DeclaredCapabilities));
    }

    public async Task<ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>> ValidateCredentialAsync(ProviderOperationContext context, CredentialMaterial credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(credential);
        ValidatedOperationRuntime.RequireSlotAndLease(context);
        var client = CreateClient(context.ProviderInstance);
        var request = new HttpRequestMessage(HttpMethod.Get, "whoami-v2");
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

    // GET {base}/models?search={q}&limit=100&skip={offset} (+ datasets second pass merged).
    // Continuation JSON {offset, query} bound to AdapterVersion.

    public async IAsyncEnumerable<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>> SearchAsync(
        ProviderOperationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Credential is null) ValidatedOperationRuntime.RequireSlot(context);
        else ValidatedOperationRuntime.RequireSlotAndLease(context);

        if (context.ContinuationAdapterVersion != null && !string.Equals(context.ContinuationAdapterVersion, AdapterVersion, StringComparison.Ordinal))
        {
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(ProviderOutcomeKind.RequestInvalid, new NormalizedSearchPage([]), context.Continuation, SanitizedCode: "IncompatibleContinuation");
            yield break;
        }

        var offset = 0;
        var effectiveQuery = context.SearchQuery ?? string.Empty;
        var malformed = false;
        if (!string.IsNullOrWhiteSpace(context.Continuation))
        {
            try
            {
                using var doc = JsonDocument.Parse(context.Continuation);
                if (doc.RootElement.TryGetProperty("offset", out var o) && o.TryGetInt32(out var po)) offset = Math.Max(0, po);
                else if (doc.RootElement.TryGetProperty("page", out var p) && p.TryGetInt32(out var pp)) offset = Math.Max(0, (pp - 1) * 100);
                if (doc.RootElement.TryGetProperty("query", out var q))
                {
                    var cq = q.GetString();
                    if (!string.IsNullOrWhiteSpace(cq)) effectiveQuery = cq;
                }
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
        var results = new List<object>();

        // Two Hub collections: models then datasets. Each is one paginated page per SearchAsync yield.
        foreach (var collection in new[] { "models", "datasets" })
        {
            var requestUri = $"{collection}?search={Uri.EscapeDataString(effectiveQuery)}&limit={limit}&skip={offset}&sort=lastModified&direction=-1";
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

                using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var jsonDoc = await JsonDocument.ParseAsync(bodyStream, default, cancellationToken);
                if (jsonDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in jsonDoc.RootElement.EnumerateArray())
                    {
                        var repoId = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                        if (string.IsNullOrWhiteSpace(repoId)) continue;
                        var sha = item.TryGetProperty("sha", out var shaProp) ? shaProp.GetString() : null;
                        var safeRev = !string.IsNullOrWhiteSpace(sha) ? sha! : "main";
                        // Hub enumeration yields repo identity; file-level discovery happens via
                        // sibling listing + content fetch. Record repo root as normalized result
                        // with deterministic revision so dedup remains (instance, repo, rev, path).
                        var baseUri = $"{context.ProviderInstance.Scheme}://{context.ProviderInstance.Host}:{context.ProviderInstance.Port}{context.ProviderInstance.BasePath.TrimEnd('/')}";
                        var provenance = $"{baseUri}/{collection}/{repoId}";
                        var slash = repoId.IndexOf('/');
                        results.Add(new ProviderResultInput(
                            ProviderKind: SearchProviderEnum.HuggingFace,
                            ProviderInstanceStableId: context.ProviderInstance.StableId,
                            RepositoryStableId: $"{collection}/{repoId}",
                            ImmutableRevisionOrEquivalentVersion: safeRev,
                            NormalizedFilePath: "repo-card",
                            RepositoryOwner: slash > 0 ? repoId[..slash] : repoId,
                            RepositoryName: slash > 0 ? repoId[(slash + 1)..] : repoId,
                            FileName: "README.md",
                            Snippet: item.TryGetProperty("pipeline_tag", out var pt) ? pt.GetString() : null,
                            ProvenanceUrl: provenance,
                            Branch: safeRev));
                        if (results.Count >= context.Bounds.MaxEventCount) break;
                    }
                }

                if (results.Count >= context.Bounds.MaxEventCount) break;
            }
        }

        string? nextContinuation = results.Count >= limit
            ? JsonSerializer.Serialize(new { offset = offset + limit, query = effectiveQuery })
            : null;

        yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
            ProviderOutcomeKind.Success, new NormalizedSearchPage(results), nextContinuation);
    }

    public async Task<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>> FetchContentAsync(ProviderOperationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Credential is null) ValidatedOperationRuntime.RequireSlot(context);
        else ValidatedOperationRuntime.RequireSlotAndLease(context);

        var requestUrl = ResolveContentUrl(context);
        if (requestUrl is null)
            return new ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>(ProviderOutcomeKind.RequestInvalid, null, SanitizedCode: "MissingContentLocation");

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
        // Hub raw: /{repo}/raw/{rev}/{path}. Repository fields carry "owner/name".
        var repo = !string.IsNullOrWhiteSpace(context.ContentRepositoryName)
            ? $"{context.ContentRepositoryOwner}/{context.ContentRepositoryName}".Trim('/')
            : context.ContentRepositoryOwner;
        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(context.ContentPath)) return null;
        var safeRev = !string.IsNullOrWhiteSpace(context.ContentRevision) ? context.ContentRevision! : "main";
        var baseUri = $"{context.ProviderInstance.Scheme}://{context.ProviderInstance.Host}:{context.ProviderInstance.Port}{context.ProviderInstance.BasePath.TrimEnd('/')}";
        // Hub raw lives one level above /api: strip trailing /api for raw host.
        var rawBase = baseUri.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? baseUri[..^4] : baseUri;
        return $"{rawBase}/{repo.Trim('/')}/raw/{Uri.EscapeDataString(safeRev)}/{context.ContentPath.TrimStart('/')}";
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
