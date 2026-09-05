using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Providers.Search_Providers
{
    /// <summary>
    /// Implements the ISearchProvider interface for searching code on GitLab.
    /// </summary>
    public class GitLabSearchProvider : ISearchProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GitLabSearchProvider>? _logger;
        private const string GitLabApiBaseUrl = "https://gitlab.com/api/v4";

        /// <inheritdoc />
        public string ProviderName => "GitLab";

        public GitLabSearchProvider(HttpClient? httpClient = null, ILogger<GitLabSearchProvider>? logger = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<SearchResponse> SearchAsync(SearchQuery query, SearchProviderToken? token, string? extraQueryParams, int startPage = 1)
        {
            if (token == null || string.IsNullOrWhiteSpace(token.Token))
            {
                _logger?.LogError("GitLab token is missing or invalid.");
                throw new ArgumentNullException(nameof(token), "A valid GitLab token (PAT) is required.");
            }

            if (query == null || string.IsNullOrWhiteSpace(query.Query))
            {
                _logger?.LogError("Search query is missing or invalid.");
                throw new ArgumentNullException(nameof(query), "A valid search query is required.");
            }

            var results = new List<RepoReference>();
            int page = startPage;
            const int perPage = 100; // Max allowed by GitLab API
            int totalResultsCount = 0;
            bool hitLimit = false;

            string finalQuery = query.Query;
            if (!string.IsNullOrWhiteSpace(extraQueryParams))
            {
                finalQuery += " " + extraQueryParams;
            }

            _logger?.LogInformation("Starting GitLab code search for query: {Query} (Starting at page {Page})", finalQuery, page);

            try
            {
                while (true)
                {
                    // Build search URL
                    // Endpoint: /api/v4/search?scope=blobs&search={query}&page={page}&per_page={perPage}
                    var searchUrl = $"{GitLabApiBaseUrl}/search?scope=blobs&search={Uri.EscapeDataString(finalQuery)}&page={page}&per_page={perPage}";

                    using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                    request.Headers.Add("PRIVATE-TOKEN", token.Token);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                    request.Headers.UserAgent.ParseAdd("UnsecuredAPIKeys-Scraper/1.0");

                    using var response = await _httpClient.SendAsync(request);

                    // Rate limit detection
                    if (response.StatusCode == (HttpStatusCode)429)
                    {
                        var resetTime = DateTime.UtcNow.AddMinutes(1);
                        if (response.Headers.TryGetValues("RateLimit-Reset", out var resetValues) &&
                            long.TryParse(resetValues.FirstOrDefault(), out var epochSeconds))
                        {
                            resetTime = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
                        }

                        _logger?.LogWarning("GitLab API rate limit exceeded. Reset at: {ResetTime}", resetTime);
                        throw new HttpRequestException($"GitLab API rate limit exceeded. Reset at {resetTime:yyyy-MM-dd HH:mm:ss} UTC", null, HttpStatusCode.TooManyRequests);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorBody = await response.Content.ReadAsStringAsync();
                        _logger?.LogError("GitLab API error on page {Page}. Status: {StatusCode}. Response: {Response}", page, response.StatusCode, errorBody);
                        break;
                    }

                    // Extract total count headers if available
                    if (response.Headers.TryGetValues("X-Total", out var totalValues) &&
                        int.TryParse(totalValues.FirstOrDefault(), out var total))
                    {
                        totalResultsCount = total;
                    }

                    var jsonStream = await response.Content.ReadAsStreamAsync();
                    var blobItems = await JsonSerializer.DeserializeAsync<List<GitLabBlobSearchResult>>(jsonStream, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (blobItems == null || blobItems.Count == 0)
                    {
                        _logger?.LogInformation("No more results found for query '{Query}' on page {Page}.", finalQuery, page);
                        break;
                    }

                    _logger?.LogDebug("Found {Count} results on page {Page} for query '{Query}'.", blobItems.Count, page, finalQuery);

                    foreach (var item in blobItems)
                    {
                        var safeBranch = !string.IsNullOrWhiteSpace(item.Ref) ? item.Ref : "main";
                        var encodedPath = Uri.EscapeDataString(item.Path ?? string.Empty);

                        // Construct URLs for viewing and downloading content
                        var apiRawUrl = $"{GitLabApiBaseUrl}/projects/{item.ProjectId}/repository/files/{encodedPath}/raw?ref={Uri.EscapeDataString(safeBranch)}";
                        var fileWebUrl = $"https://gitlab.com/projects/{item.ProjectId}/-/blob/{safeBranch}/{item.Path}";
                        var repoWebUrl = $"https://gitlab.com/projects/{item.ProjectId}";

                        results.Add(new RepoReference
                        {
                            SearchQueryId = query.Id,
                            Provider = ProviderName,
                            RepoId = item.ProjectId,
                            RepoOwner = $"gitlab-project-{item.ProjectId}",
                            RepoName = $"project-{item.ProjectId}",
                            FilePath = item.Path,
                            FileName = item.Filename ?? (!string.IsNullOrEmpty(item.Path) ? Path.GetFileName(item.Path) : "unknown"),
                            FileURL = fileWebUrl,
                            ApiContentUrl = apiRawUrl,
                            Branch = safeBranch,
                            CodeContext = item.Data,
                            LineNumber = item.Startline,
                            FoundUTC = DateTime.UtcNow,
                            RepoURL = repoWebUrl,
                            RepoDescription = $"GitLab Project #{item.ProjectId}"
                        });
                    }

                    // Safety limit to avoid unbounded pagination
                    if (results.Count >= 1000)
                    {
                        hitLimit = true;
                        _logger?.LogWarning("Hit 1,000 result limit for query '{Query}'.", finalQuery);
                        break;
                    }

                    if (blobItems.Count < perPage)
                    {
                        break;
                    }

                    page++;
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "An unexpected error occurred during GitLab search for query: {Query}", query.Query);
            }

            if (totalResultsCount == 0)
            {
                totalResultsCount = results.Count;
            }

            _logger?.LogInformation("Completed GitLab search for query '{Query}'. Found {Count}/{Total} match(es).", query.Query, results.Count, totalResultsCount);
            return new SearchResponse(results, page, totalResultsCount, hitLimit);
        }

        private sealed class GitLabBlobSearchResult
        {
            [JsonPropertyName("basename")]
            public string? Basename { get; set; }

            [JsonPropertyName("data")]
            public string? Data { get; set; }

            [JsonPropertyName("path")]
            public string? Path { get; set; }

            [JsonPropertyName("filename")]
            public string? Filename { get; set; }

            [JsonPropertyName("ref")]
            public string? Ref { get; set; }

            [JsonPropertyName("startline")]
            public int Startline { get; set; }

            [JsonPropertyName("project_id")]
            public long ProjectId { get; set; }
        }
    }
}
