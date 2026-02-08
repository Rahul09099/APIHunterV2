using System.ComponentModel.DataAnnotations;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Data.Models
{
    public class SearchProviderToken
    {
        [Key] public int Id { get; set; }
        public string Token { get; set; } = string.Empty;
        public SearchProviderEnum SearchProvider { get; set; } = SearchProviderEnum.Unknown;
        public bool IsEnabled { get; set; }

        // Make it nullable so we can identify never-used tokens  
        public DateTime? LastUsedUTC { get; set; }
    }
}
