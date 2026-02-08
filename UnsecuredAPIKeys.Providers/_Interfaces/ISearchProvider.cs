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
        /// Executes a search based on the provided query.
        /// </summary>
        /// <param name="query">The search query details.</param>
        /// <param name="token">The API token to use for the search.</param>
        /// <returns>A collection of RepoReference objects representing potential findings.</returns>
        Task<IEnumerable<RepoReference>> SearchAsync(SearchQuery query, SearchProviderToken? token);
    }
}
