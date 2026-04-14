using System.ComponentModel.DataAnnotations;

namespace UnsecuredAPIKeys.Data.Models
{
    public class TelegramSubscriber
    {
        [Key]
        public long TelegramId { get; set; }

        public string? Username { get; set; }

        public bool IsAdmin { get; set; } = false;

        public DateTime SubscriptionExpiryUtc { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        // Ghost Node tracking
        public string? NodeToken { get; set; }
        public string? NodeUrl { get; set; }
        public DateTime? LastNodeHeartbeatUtc { get; set; }
    }
}
