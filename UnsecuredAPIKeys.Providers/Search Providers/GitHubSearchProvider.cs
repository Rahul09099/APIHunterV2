using Microsoft.Extensions.Logging;
using Octokit;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Data.Common;
// Assuming logging might be needed later

namespace UnsecuredAPIKeys.Providers.Search_Providers
{
    /// <summary>
    /// Implements the ISearchProvider interface for searching code on GitHub.
    /// </summary>
    public class GitHubSearchProvider(ILogger<GitHubSearchProvider>? logger = null) : ISearchProvider
    {
        /// <inheritdoc />
        public string ProviderName => "GitHub";

        /// <inheritdoc />
        public async Task<SearchResponse> SearchAsync(SearchQuery query, SearchProviderToken? token, string? extraQueryParams, int startPage = 1)
        {
            if (token == null || string.IsNullOrWhiteSpace(token.Token))
            {
                logger?.LogError("GitHub token is missing or invalid.");
                throw new ArgumentNullException(nameof(token), "A valid GitHub token is required.");
            }

            if (query == null || string.IsNullOrWhiteSpace(query.Query))
            {
                logger?.LogError("Search query is missing or invalid.");
                throw new ArgumentNullException(nameof(query), "A valid search query is required.");
            }

            var client = new GitHubClient(new ProductHeaderValue("UnsecuredAPIKeys-Scraper"))
            {
                Credentials = new Credentials(token.Token)
            };

            var results = new List<RepoReference>();
            int page = startPage;
            const int perPage = 100; // Max allowed by GitHub API
            int totalResultsCount = 0;
            bool hitLimit = false;

            try
            {
                // Clean input query
                string finalQuery = query.Query;
                if (!string.IsNullOrWhiteSpace(extraQueryParams))
                {
                    finalQuery += " " + extraQueryParams;
                }

                logger?.LogInformation("Starting GitHub search for query: {Query} (Starting at page {Page})", finalQuery, page);

                while (true) // Loop to handle pagination
                {
                    var request = new SearchCodeRequest(finalQuery)
                    {
                        Page = page,
                        PerPage = perPage
                    };

                    SearchCodeResult searchResult;
                    try
                    {
                        searchResult = await client.Search.SearchCode(request);
                        totalResultsCount = searchResult.TotalCount;

                    }
                    catch (RateLimitExceededException ex)
                    {
                        var resetTime = ex.Reset.LocalDateTime.ToIst();
                        logger?.LogWarning("GitHub API rate limit exceeded for this token. Reset at: {ResetTime}", resetTime);
                        Console.WriteLine($"[yellow]GitHub API rate limit exceeded for this token. Reset at: {resetTime:HH:mm:ss} IST[/]");
                        throw;
                    }
                    catch (ApiException apiEx)
                    {
                        logger?.LogError(apiEx, "GitHub API error during search on page {Page}. Status: {StatusCode}", page, apiEx.StatusCode);
                        Console.WriteLine($"[red]GitHub API error during search on page {page}. Status: {apiEx.StatusCode}. Reason: {apiEx.Message}[/]");
                        break; 
                    }

                    if (searchResult?.Items == null || !searchResult.Items.Any())
                    {
                        logger?.LogInformation("No more results found for query '{Query}' on page {Page}.", finalQuery, page);
                        break;
                    }

                    logger?.LogDebug("Found {Count} results on page {Page} for query '{Query}'.", searchResult.Items.Count, page, finalQuery);

                    foreach (var item in searchResult.Items)
                    {
                        results.Add(new RepoReference
                        {
                            SearchQueryId = query.Id,
                            Provider = ProviderName,
                            RepoOwner = item.Repository?.Owner?.Login,
                            RepoName = item.Repository?.Name,
                            FilePath = item.Path,
                            FileURL = item.HtmlUrl,
                            ApiContentUrl = item.Url,
                            Branch = item.Repository?.DefaultBranch,
                            FileSHA = item.Sha,
                            FoundUTC = DateTime.UtcNow,
                            RepoURL = item.Repository?.HtmlUrl,
                            RepoDescription = item.Repository?.Description,
                            FileName = item.Name
                        });
                    }

                    // Check if we reached the GitHub limit
                    if (results.Count >= 1000)
                    {
                        hitLimit = true;
                        logger?.LogWarning("Hit GitHub's 1,000 result limit for query '{Query}'. Further results are unreachable without partitioning.", finalQuery);
                        break;
                    }

                    if (searchResult.Items.Count < perPage)
                    {
                        break;
                    }

                    page++; 
                    await Task.Delay(TimeSpan.FromSeconds(2)); 
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "An unexpected error occurred during GitHub search for query: {Query}", query.Query);
                Console.WriteLine($"[red]An unexpected error occurred during GitHub search for query '{query.Query}': {ex.Message}[/]");
            }

            logger?.LogInformation("Completed GitHub search for query '{Query}'. Found {Count}/{Total} match(es).", query.Query, results.Count, totalResultsCount);
            return new SearchResponse(results, page, totalResultsCount, hitLimit);
        }
    }
}
