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
/// Versioned Azure DevOps adapter (Phase 4, Task 21.2).
/// Explicit organization/project/repository configuration via instance settings;
/// scoped code search (POST {org}/{project}/_apis/search/codesearchresults?api-version=7.1).
/// No global-public capability is assumed or exposed.
/// </summary>
public sealed class AzureDevOpsSearchProviderAdapter : ISearchProviderAdapter
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _directClient;
    private readonly ISearchProviderOutcomeClassifier _classifier;

    public SearchProviderEnum ProviderKind => SearchProviderEnum.AzureDevOps;
    public string AdapterVersion => "azuredevops-search-v1";
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
            new ProviderSettingDefinition("Organization", "string", true, "Required Azure DevOps organization (non-secret)."),
            new ProviderSettingDefinition("Project", "string", false, "Optional default project scope (non-secret).")
        ]);

    public AzureDevOpsSearchProviderAdapter() : this(httpClientFactory: null, classifier: null) { }
    public AzureDevOpsSearchProviderAdapter(HttpClient httpClient, ISearchProviderOutcomeClassifier? classifier = null)
    {
        _directClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _classifier = classifier ?? new AzureDevOpsResponseClassifier();
    }
    [ActivatorUtilitiesConstructor]
    public AzureDevOpsSearchProviderAdapter(IHttpClientFactory? httpClientFactory, ISearchProviderOutcomeClassifier? classifier = null)
    {
        _httpClientFactory = httpClientFactory;
        _classifier = classifier ?? new AzureDevOpsResponseClassifier();
    }

    private HttpClient CreateClient(ValidatedProviderInstance instance)
    {
        if (_directClient is not null) return _directClient;
        var client = _httpClientFactory?.CreateClient("AzureDevOpsSearch") ?? new HttpClient();
        var baseUri = $"{instance.Scheme}://{instance.Host}:{instance.Port}{instance.BasePath.TrimEnd('/')}/";
        if (client.BaseAddress is null) client.BaseAddress = new Uri(baseUri);
        return client;
    }

    private static void ApplyCredentialHeaders(HttpRequestMessage request, CredentialMaterial? credential)
    {
        if (credential is null || string.IsNullOrWhiteSpace(credential.Value)) return;
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{credential.Value}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }

    public Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(ValidatedProviderInstance instance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.ProviderKind != SearchProviderEnum.AzureDevOps)
            return Task.FromResult(new ProviderCapabilityResult(ProviderOutcomeKind.RequestInvalid, SearchProviderCapability.None, SanitizedCode: "KindMismatch"));
        return Task.FromResult(new ProviderCapabilityResult(ProviderOutcomeKind.Success, DeclaredCapabilities));
    }

    public async Task<ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>> ValidateCredentialAsync(ProviderOperationContext context, CredentialMaterial credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(credential);
        ValidatedOperationRuntime.RequireSlotAndLease(context);
        var client = CreateClient(context.ProviderInstance);
        var request = new HttpRequestMessage(HttpMethod.Get, "_apis/connectionData?api-version=7.1");
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
        var skip = 0;
        var effectiveQuery = context.SearchQuery ?? string.Empty;
        var malformed = false;
        if (!string.IsNullOrWhiteSpace(context.Continuation))
        {
            try
            {
                using var doc = JsonDocument.Parse(context.Continuation);
                if (doc.RootElement.TryGetProperty("offset", out var o) && o.TryGetInt32(out var po)) skip = Math.Max(0, po);
                else if (doc.RootElement.TryGetProperty("page", out var p) && p.TryGetInt32(out var pp)) skip = Math.Max(0, (pp - 1) * 100);
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
        const int top = 100;
        // Scoped path: {org}/{project}/_apis/search/codesearchresults?api-version=7.1
        // Organization/project come from validated settings; query carries optional filters.
        var (org, project) = ParseOrgProject(context.ProviderInstance.SettingsJson);
        var scopePrefix = !string.IsNullOrWhiteSpace(org) ? $"{Uri.EscapeDataString(org)}/" : string.Empty;
        if (!string.IsNullOrWhiteSpace(project)) scopePrefix += $"{Uri.EscapeDataString(project)}/";
        var requestUri = $"{scopePrefix}_apis/search/codesearchresults?api-version=7.1";
        var payload = JsonSerializer.Serialize(new { searchText = effectiveQuery, skip, top });
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
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
            else if (root.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array)
            {
                arr = r.EnumerateArray().ToArray();
            }
            else if (root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array)
            {
                arr = v.EnumerateArray().ToArray();
            }
            else
            {
                arr = Array.Empty<JsonElement>();
            }
            foreach (var item in arr)
            {
                var projectName = item.TryGetProperty("project", out var pj) ? (pj.TryGetProperty("name", out var pn) ? pn.GetString() ?? string.Empty : pj.GetString() ?? string.Empty) : (org ?? string.Empty);
                var repoName = item.TryGetProperty("repository", out var rp) ? (rp.TryGetProperty("name", out var rn) ? rn.GetString() ?? string.Empty : rp.GetString() ?? string.Empty) : string.Empty;
                var repoId = item.TryGetProperty("repository", out var rp2) ? (rp2.TryGetProperty("id", out var rid) ? rid.GetString() ?? string.Empty : string.Empty) : string.Empty;
                var path = item.TryGetProperty("path", out var pp) ? pp.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(path)) continue;
                var version = item.TryGetProperty("version", out var vp) ? vp.GetString() : null;
                var safeRev = !string.IsNullOrWhiteSpace(version) ? version! : "main";
                var stableRepo = !string.IsNullOrWhiteSpace(repoId) ? repoId : $"{projectName}/{repoName}";
                var baseUri = $"{context.ProviderInstance.Scheme}://{context.ProviderInstance.Host}:{context.ProviderInstance.Port}{context.ProviderInstance.BasePath.TrimEnd('/')}";
                var provenance = $"{baseUri}/{Uri.EscapeDataString(projectName)}/_git/{Uri.EscapeDataString(repoName)}?path={Uri.EscapeDataString(path)}&version={Uri.EscapeDataString(safeRev)}";
                results.Add(new ProviderResultInput(
                    ProviderKind: SearchProviderEnum.AzureDevOps,
                    ProviderInstanceStableId: context.ProviderInstance.StableId,
                    RepositoryStableId: stableRepo,
                    ImmutableRevisionOrEquivalentVersion: safeRev,
                    NormalizedFilePath: path,
                    RepositoryOwner: projectName,
                    RepositoryName: repoName,
                    FileName: Path.GetFileName(path),
                    Snippet: item.TryGetProperty("content", out var cp) ? cp.GetString() : null,
                    ProvenanceUrl: provenance,
                    Branch: safeRev));
                if (results.Count >= context.Bounds.MaxEventCount) break;
            }
            string? next = results.Count >= top ? JsonSerializer.Serialize(new { offset = skip + top, query = effectiveQuery }) : null;
            yield return new ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>(ProviderOutcomeKind.Success, new NormalizedSearchPage(results), next);
        }
    }

    public async Task<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>> FetchContentAsync(ProviderOperationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Credential is null) ValidatedOperationRuntime.RequireSlot(context);
        else ValidatedOperationRuntime.RequireSlotAndLease(context);
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

    internal static (string? Org, string? Project) ParseOrgProject(string settingsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson);
            string? org = null, project = null;
            if (doc.RootElement.TryGetProperty("Organization", out var o)) org = o.GetString();
            if (doc.RootElement.TryGetProperty("organization", out var o2)) org ??= o2.GetString();
            if (doc.RootElement.TryGetProperty("Project", out var p)) project = p.GetString();
            if (doc.RootElement.TryGetProperty("project", out var p2)) project ??= p2.GetString();
            return (org, project);
        }
        catch { return (null, null); }
    }

    private static string? ResolveContentUrl(ProviderOperationContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ContentApiUrl)) return context.ContentApiUrl;
        if (string.IsNullOrWhiteSpace(context.ContentPath)) return null;
        var (org, project) = ParseOrgProject(context.ProviderInstance.SettingsJson);
        var owner = !string.IsNullOrWhiteSpace(context.ContentRepositoryOwner) ? context.ContentRepositoryOwner! : project ?? org;
        var repo = !string.IsNullOrWhiteSpace(context.ContentRepositoryName) ? context.ContentRepositoryName! : null;
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo)) return null;
        var safeRev = !string.IsNullOrWhiteSpace(context.ContentRevision) ? context.ContentRevision! : "main";
        var baseUri = $"{context.ProviderInstance.Scheme}://{context.ProviderInstance.Host}:{context.ProviderInstance.Port}{context.ProviderInstance.BasePath.TrimEnd('/')}";
        var orgPrefix = !string.IsNullOrWhiteSpace(org) ? $"{Uri.EscapeDataString(org)}/" : string.Empty;
        return $"{baseUri}/{orgPrefix}{Uri.EscapeDataString(owner)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/items?path={Uri.EscapeDataString(context.ContentPath)}&version={Uri.EscapeDataString(safeRev)}&api-version=7.1";
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
