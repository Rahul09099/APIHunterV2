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
    }
}
