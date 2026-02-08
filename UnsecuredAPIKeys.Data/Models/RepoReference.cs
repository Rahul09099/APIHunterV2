using System.ComponentModel.DataAnnotations;

namespace UnsecuredAPIKeys.Data.Models
{
    public class RepoReference
    {
        [Key]
        public long Id { get; set; }
        public long APIKeyId { get; set; }  // Foreign key to APIKey
        public virtual APIKey? APIKey { get; set; }  // Navigation property

        // Repository information
        [Required]
        public string? RepoURL { get; set; }  // Just base repo URL
        public string? RepoOwner { get; set; }  // Owner username/organization
        public string? RepoName { get; set; }  // Repository name
        public string? RepoDescription { get; set; }
        public long RepoId { get; set; }  // GitHub's repo ID

        // File information
        [Required]
        public string? FileURL { get; set; }  // Full path with commit hash
        public string? FileName { get; set; }
        public string? FilePath { get; set; }  // Path within repo
        public string? FileSHA { get; set; }
        public string? ApiContentUrl { get; set; } // URL to fetch raw content via API

        // Context information
        public string? CodeContext { get; set; }  // Surrounding code
        public int LineNumber { get; set; }

        // Discovery metadata
        public long SearchQueryId { get; set; }  // Which query found this
        public DateTime FoundUTC { get; set; }
        public string? Provider { get; set; } // e.g., GitHub, GitLab
        public string? Branch { get; set; } // Branch where the file was found
    }
}
