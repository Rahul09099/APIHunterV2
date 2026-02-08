using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using UnsecuredAPIKeys.Data.Common;
using System.Text.Json.Serialization; // <-- Add this using directive

namespace UnsecuredAPIKeys.Data.Models
{
    public class APIKey
    {
        [Key]
        public long Id { get; set; }

        [Required]
        public required string ApiKey { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ApiStatusEnum Status { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ApiTypeEnum ApiType { get; set; } = ApiTypeEnum.Unknown;

        public SearchProviderEnum SearchProvider { get; set; }

        public DateTime? LastCheckedUTC { get; set; }
        public DateTime FirstFoundUTC { get; set; }
        public DateTime LastFoundUTC { get; set; }

        public int TimesDisplayed { get; set; }
        
        // Error tracking for verification failures
        public int ErrorCount { get; set; } = 0;

        // Validation response or error message
        public string? ValidationResponse { get; set; }

        // Navigation property to where this key was found
        public virtual ICollection<RepoReference> References { get; set; } = [];
    }
}
