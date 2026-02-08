using System.ComponentModel.DataAnnotations;

namespace UnsecuredAPIKeys.Data.Models;

/// <summary>
/// Tracks progress for Deep Search partitions (language/extension combinations)
/// </summary>
public class DeepSearchProgress
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to SearchQuery
    /// </summary>
    /// <summary>
    /// Foreign key to SearchQuery
    /// </summary>
    public long SearchQueryId { get; set; }

    /// <summary>
    /// Type of partition: "language" or "extension"
    /// </summary>
    public required string PartitionType { get; set; }

    /// <summary>
    /// Value of the partition (e.g., "python", "env")
    /// </summary>
    public required string PartitionValue { get; set; }

    /// <summary>
    /// Last page number that was successfully searched
    /// </summary>
    public int LastPageSearched { get; set; } = 0;

    /// <summary>
    /// Total results found for this partition
    /// </summary>
    public int TotalResultsFound { get; set; } = 0;

    /// <summary>
    /// Whether this partition has been fully searched (hit the end or 1000 result limit)
    /// </summary>
    public bool IsCompleted { get; set; } = false;

    /// <summary>
    /// When this partition was last searched
    /// </summary>
    public DateTime LastSearchedUTC { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property to SearchQuery
    /// </summary>
    public SearchQuery? SearchQuery { get; set; }
}
