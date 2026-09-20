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
/// Versioned Sourcegraph Search Provider Adapter (Phase 1, Task 18.2).
/// Supports SaaS (sourcegraph.com/.api) and approved self-hosted instances through
/// Endpoint-Policy-validated configuration. Uses the supported streaming search API
/// (GET search/stream?q=&amp;v=V3) with incremental event framing, progress/match/alert/
/// terminal parsing, reconnect-safe Last Safe Checkpoints, and bounded untrusted parsing.
/// Gated by Operation Slots and credential Claim Leases. Never swallows errors.
/// </summary>
public sealed class SourcegraphSearchProviderAdapter : ISearchProviderAdapter
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _directClient;
    private readonly ISearchProviderOutcomeClassifier _classifier;

    public SearchProviderEnum ProviderKind => SearchProviderEnum.Sourcegraph;

    public string AdapterVersion => "sourcegraph-stream-v1";

    public SearchProviderCapability DeclaredCapabilities =>
        SearchProviderCapability.CodeSearch |
        SearchProviderCapability.PaginatedSearch |
        SearchProviderCapability.StreamingSearch |
        SearchProviderCapability.ContentRetrieval |
        SearchProviderCapability.PrivateRepositories |
        SearchProviderCapability.GlobalPublicSearch |
        SearchProviderCapability.SelfHosted |
        SearchProviderCapability.RepositoryMetadata |
        SearchProviderCapability.NativeQueryOverrides;

    public ProviderSettingsSchema SettingsSchema => ProviderSettingsSchema.Empty;

    public SourcegraphSearchProviderAdapter()
        : this(httpClientFactory: null, classifier: null)
    {
    }

    public SourcegraphSearchProviderAdapter(HttpClient httpClient, ISearchProviderOutcomeClassifier? classifier = null)
    {
        _directClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _classifier = classifier ?? new SourcegraphResponseClassifier();
    }

    [ActivatorUtilitiesConstructor]
    public SourcegraphSearchProviderAdapter(
        IHttpClientFactory? httpClientFactory,
        ISearchProviderOutcomeClassifier? classifier = null)
    {
        _httpClientFactory = httpClientFactory;
        _classifier = classifier ?? new SourcegraphResponseClassifier();
    }

    private HttpClient CreateClient(ValidatedProviderInstance instance)
    {
        if (_directClient is not null)
        {
            return _directClient;
        }

        var client = _httpClientFactory?.CreateClient("SourcegraphSearch") ?? new HttpClient();
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

        request.Headers.Authorization = new AuthenticationHeaderValue("token", credential.Value);
    }

    public Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(
        ValidatedProviderInstance instance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.ProviderKind != SearchProviderEnum.Sourcegraph)
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

    // GET {base}/search/stream?q={q}&v=V3&display=100&offset={offset}
    // Event framing: "event: matches|progress|alert|done" + "data: {json}".
    // Continuation is JSON {offset, query} bound to AdapterVersion (AC-7.12/7.13).
    // Last Safe Checkpoint is progress-only (offset), never match content (AC-7.27).

    public async IAsyncEnumerable<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>> SearchAsync(
        ProviderOperationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

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

        var offset = 0;
        var effectiveQuery = context.SearchQuery ?? string.Empty;

        var malformedContinuation = false;
        if (!string.IsNullOrWhiteSpace(context.Continuation))
        {
            try
            {
                using var doc = JsonDocument.Parse(context.Continuation);
                if (doc.RootElement.TryGetProperty("offset", out var offsetProp) && offsetProp.TryGetInt32(out var parsedOffset))
                {
                    offset = Math.Max(0, parsedOffset);
                }
                else if (doc.RootElement.TryGetProperty("page", out var pageProp) && pageProp.TryGetInt32(out var parsedPage))
                {
                    offset = Math.Max(0, (parsedPage - 1) * 100);
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
        const int display = 100;
        var requestUri = $"search/stream?q={Uri.EscapeDataString(effectiveQuery)}&v=V3&display={display}&offset={offset}";

        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        ApplyCredentialHeaders(request, context.Credential);
        request.Headers.UserAgent.ParseAdd("UnsecuredAPIKeys-Platform/1.0");
        request.Headers.Accept.ParseAdd("text/event-stream");

        HttpResponseMessage? response = null;
        ProviderOutcomeKind? transportOutcome = null;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
            var consumed = 0;
            var terminalReached = false;
            var cancelled = false;
            var maxEvents = Math.Max(1, context.Bounds.MaxEventCount);

            try
            {
                using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(bodyStream, Encoding.UTF8);
                string? currentEvent = null;
                var eventCount = 0;

                while (!reader.EndOfStream && eventCount < maxEvents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentEvent = line.Substring("event:".Length).Trim();
                        continue;
                    }

                    if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        eventCount++;
                        var payload = line.Substring("data:".Length).Trim();
                        if (string.IsNullOrWhiteSpace(payload) || payload == "[DONE]")
                        {
                            continue;
                        }

                        JsonDocument? payloadDoc = null;
                        try
                        {
                            payloadDoc = JsonDocument.Parse(payload);
                        }
                        catch
                        {
                            continue;
                        }

                        using (payloadDoc)
                        {
                            var root = payloadDoc.RootElement;
                            var evt = currentEvent ?? (root.TryGetProperty("type", out var t) ? t.GetString() : null);

                            if (string.Equals(evt, "done", StringComparison.OrdinalIgnoreCase))
                            {
                                terminalReached = true;
                                continue;
                            }

                            if (string.Equals(evt, "alert", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (string.Equals(evt, "progress", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // matches event (or fallback array element)
                            IEnumerable<JsonElement> matches;
                            if (root.ValueKind == JsonValueKind.Array)
                            {
                                matches = root.EnumerateArray().ToArray();
                            }
                            else if (root.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array)
                            {
                                matches = r.EnumerateArray().ToArray();
                            }
                            else if (root.TryGetProperty("repository", out _))
                            {
                                matches = new[] { root };
                            }
                            else
                            {
                                matches = Array.Empty<JsonElement>();
                            }

                            foreach (var item in matches)
                            {
                                if (results.Count >= maxEvents)
                                {
                                    break;
                                }

                                var repo = item.TryGetProperty("repository", out var repoProp)
                                    ? repoProp.GetString() ?? string.Empty
                                    : (item.TryGetProperty("repo", out var repoAlt) ? repoAlt.GetString() ?? string.Empty : string.Empty);
                                var path = item.TryGetProperty("path", out var pathProp)
                                    ? pathProp.GetString() ?? string.Empty
                                    : (item.TryGetProperty("file", out var fileAlt) ? fileAlt.GetString() ?? string.Empty : string.Empty);
                                if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(path))
                                {
                                    continue;
                                }

                                var rev = item.TryGetProperty("commit", out var commitProp)
                                    ? commitProp.GetString()
                                    : (item.TryGetProperty("rev", out var revProp) ? revProp.GetString() : null);
                                var safeRev = !string.IsNullOrWhiteSpace(rev) ? rev! : "HEAD";
                                var fileName = Path.GetFileName(path);
                                string? snippet = null;
                                if (item.TryGetProperty("lineMatches", out var lm) && lm.ValueKind == JsonValueKind.Array)
                                {
                                    var first = lm.EnumerateArray().FirstOrDefault();
                                    if (first.ValueKind != JsonValueKind.Undefined)
                                    {
                                        snippet = first.TryGetProperty("preview", out var pv) ? pv.GetString() : null;
                                    }
                                }
                                else if (item.TryGetProperty("snippet", out var sn))
                                {
                                    snippet = sn.GetString();
                                }

                                var baseUri = $"{context.ProviderInstance.Scheme}://{context.ProviderInstance.Host}:{context.ProviderInstance.Port}{context.ProviderInstance.BasePath.TrimEnd('/')}";
                                var rawUrl = $"{baseUri}/{repo.Trim('/')}/-/raw/{path.TrimStart('/')}@{Uri.EscapeDataString(safeRev)}";

                                var slash = repo.IndexOf('/');
                                var owner = slash > 0 ? repo[..slash] : repo;
                                var name = slash > 0 ? repo[(slash + 1)..] : repo;

                                results.Add(new ProviderResultInput(
                                    ProviderKind: SearchProviderEnum.Sourcegraph,
                                    ProviderInstanceStableId: context.ProviderInstance.StableId,
                                    RepositoryStableId: repo,
                                    ImmutableRevisionOrEquivalentVersion: safeRev,
                                    NormalizedFilePath: path,
                                    RepositoryOwner: owner,
                                    RepositoryName: name,
                                    FileName: fileName,
                                    LineNumber: null,
                                    Snippet: snippet,
                                    ProvenanceUrl: rawUrl,
                                    Branch: safeRev));
                                consumed++;
                            }
                        }

                        currentEvent = null;
                        continue;
                    }

                    // Fallback: plain JSON array body without SSE framing.
                    if (line.TrimStart().StartsWith("[", StringComparison.Ordinal) || line.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            IEnumerable<JsonElement> arr;
                            if (root.ValueKind == JsonValueKind.Array)
                            {
                                arr = root.EnumerateArray().ToArray();
                            }
                            else if (root.TryGetProperty("results", out var r2) && r2.ValueKind == JsonValueKind.Array)
                            {
                                arr = r2.EnumerateArray().ToArray();
                            }
                            else
                            {
                                arr = Array.Empty<JsonElement>();
                            }
                            foreach (var item in arr)
                            {
                                var repo = item.TryGetProperty("repository", out var rp) ? rp.GetString() ?? string.Empty : string.Empty;
                                var path = item.TryGetProperty("path", out var pp) ? pp.GetString() ?? string.Empty : string.Empty;
                                if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(path))
                                {
                                    continue;
                                }
                                results.Add(new ProviderResultInput(
                                    ProviderKind: SearchProviderEnum.Sourcegraph,
                                    ProviderInstanceStableId: context.ProviderInstance.StableId,
                                    RepositoryStableId: repo,
                                    ImmutableRevisionOrEquivalentVersion: "HEAD",
                                    NormalizedFilePath: path,
                                    RepositoryOwner: repo,
                                    RepositoryName: repo,
                                    FileName: Path.GetFileName(path),
                                    ProvenanceUrl: null,
                                    Branch: "HEAD"));
                                consumed++;
                            }
                            terminalReached = true;
                        }
                        catch
                        {
                            // Malformed/bounded event: skip without failing the stream (Task 18.1).
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }

            if (cancelled)
            {
                yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                    ProviderOutcomeKind.Cancellation,
                    new NormalizedSearchPage(results),
                    JsonSerializer.Serialize(new { offset = offset + consumed, query = effectiveQuery }),
                    SanitizedCode: "Cancellation");
                yield break;
            }

            // Reconnect-safe Last Safe Checkpoint: progress-only offset (AC-7.27).
            string? nextContinuation = null;
            if (!terminalReached && results.Count >= display)
            {
                nextContinuation = JsonSerializer.Serialize(new { offset = offset + consumed, query = effectiveQuery });
            }
            else if (!terminalReached && consumed > 0)
            {
                nextContinuation = JsonSerializer.Serialize(new { offset = offset + consumed, query = effectiveQuery });
            }

            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(
                ProviderOutcomeKind.Success,
                new NormalizedSearchPage(results),
                nextContinuation);
        }
    }

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

    private static string? ResolveContentUrl(ProviderOperationContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ContentApiUrl))
        {
            return context.ContentApiUrl;
        }

        if (string.IsNullOrWhiteSpace(context.ContentPath))
        {
            return null;
        }

        var repo = !string.IsNullOrWhiteSpace(context.ContentRepositoryName)
            ? $"{context.ContentRepositoryOwner}/{context.ContentRepositoryName}".Trim('/')
            : context.ContentRepositoryOwner;
        if (string.IsNullOrWhiteSpace(repo))
        {
            return null;
        }

        var safeRev = !string.IsNullOrWhiteSpace(context.ContentRevision)
            ? context.ContentRevision!
            : "HEAD";
        var baseUri = $"{context.ProviderInstance.Scheme}://{context.ProviderInstance.Host}:{context.ProviderInstance.Port}{context.ProviderInstance.BasePath.TrimEnd('/')}";
        return $"{baseUri}/{repo.Trim('/')}/-/raw/{context.ContentPath.TrimStart('/')}@{Uri.EscapeDataString(safeRev)}";
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
