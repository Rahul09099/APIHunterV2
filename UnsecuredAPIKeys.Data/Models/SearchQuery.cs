using System.ComponentModel.DataAnnotations;

namespace UnsecuredAPIKeys.Data.Models
{
    public class SearchQuery
    {
        [Key] public long Id { get; set; }
        public string Query { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public int SearchResultsCount { get; set; }

        public DateTime LastSearchUTC { get; set; }
        public DateTime? LastDeepSearchDateUTC { get; set; }

        /// <summary>
        /// Set only after a scrape cycle completes and actually returns ≥1 result.
        /// Used as the dynamic lower-bound for the pushed: date filter.
        /// This prevents blind spots when the scraper is down for >30 days.
        /// </summary>
        public DateTime? LastSuccessfulSearchUTC { get; set; }

        /// <summary>
        /// Tracks the most recent repository push date (GitHub's PushedAt) seen across
        /// all results in the last successful search batch.
        /// Named LastRepoPushedSeenUTC to clearly distinguish it from any GitHub index
        /// timestamp — this is the repository push date, sourced from item.Repository.PushedAt.
        /// </summary>
        public DateTime? LastRepoPushedSeenUTC { get; set; }
    }
}
