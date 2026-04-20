using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Providers._Interfaces
{
    /// <summary>
    /// Defines the contract for a search provider used to find potential API keys.
    /// </summary>
    public interface ISearchProvider
    {
        /// <summary>
        /// Gets the name of the search provider (e.g., "GitHub", "GitLab").
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// Executes a search based on the provided query with pagination support.
        /// </summary>
        Task<SearchResponse> SearchAsync(SearchQuery query, SearchProviderToken? token, string? extraQueryParams, int startPage = 1);
    }

    public record SearchResponse(IEnumerable<RepoReference> Results, int LastPageReached, int TotalResultsCount, bool HitLimit);
}
