using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Data.Models
{
    public enum RiskLevel
    {
        Critical = 0,
        High = 1,
        Medium = 2,
        Low = 3
    }

    public enum CredentialType
    {
        SSH,
        FTP,
        FTPS,
        SFTP,
        RDP,
        VNC,
        WinRM_HTTP,
        WinRM_HTTPS,
        SMTP,
        SMTP_Submission,
        SMTPS,
        IMAP,
        IMAPS,
        POP3,
        POP3S,
        MySQL,
        PostgreSQL,
        MongoDB,
        Redis,
        MSSQL,
        Kubernetes,
        Docker,
        cPanel_HTTP,
        cPanel_HTTPS,
        WHM_HTTP,
        WHM_HTTPS,
        Plesk
    }

    public record NetworkVerificationResult(
        bool IsAccessible, string Status, string? ErrorMessage = null)
    {
        public static NetworkVerificationResult Success(string host, int port) =>
            new(true, "Accessible");
        public static NetworkVerificationResult Unreachable() =>
            new(false, "Unreachable");
        public static NetworkVerificationResult Timeout() =>
            new(false, "Timeout");
        public static NetworkVerificationResult Error(string msg) =>
            new(false, "Error", msg);
    }

    public record AuthVerificationResult(
        bool IsValid, string Status, string? Details = null)
    {
        public static AuthVerificationResult Valid(string details)    => new(true,  "Valid",       details);
        public static AuthVerificationResult Invalid(string details)  => new(false, "Invalid",     details);
        public static AuthVerificationResult RateLimited()            => new(false, "RateLimited");
        public static AuthVerificationResult Error(string msg)        => new(false, "Error",       msg);
    }

    public record SslCertificateInfo(
        string Subject, string Issuer,
        DateTime ValidFrom, DateTime ValidTo, string Thumbprint)
    {
        public static SslCertificateInfo NotAvailable() =>
            new("N/A", "N/A", DateTime.MinValue, DateTime.MinValue, "N/A");
        public static SslCertificateInfo Error(string msg) =>
            new(msg, msg, DateTime.MinValue, DateTime.MinValue, msg);
    }

    public class CredentialContext
    {
        public string FullContext { get; set; } = string.Empty;
        public int MatchLine { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
    }

    public class ServerCredential
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string CredentialType { get; set; } = string.Empty;  // SSH, FTP, RDP, SMTP, cPanel, WHM …

        [Required]
        public string Host { get; set; } = string.Empty;

        public int Port { get; set; }

        public string Username { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;  // Plaintext password storage

        public string Domain { get; set; } = string.Empty;  // for RDP/WinRM

        public string NetworkStatus { get; set; } = "Unknown";     // Accessible | Unreachable | Timeout

        public string AuthenticationStatus { get; set; } = "Untested";   // Valid | Invalid | RateLimited | Error

        public string ServerMetadata { get; set; } = "{}";          // JSON: banner, version, OS, SSL

        public string GeolocationData { get; set; } = "{}";          // JSON: country, city, ISP, ASN, cloud

        public string OSINTData { get; set; } = "{}";          // JSON: Shodan, Censys, GreyNoise

        public string RiskLevel { get; set; } = "Low";         // Critical | High | Medium | Low

        public bool IsHoneypot { get; set; }

        public string SourceRepository { get; set; } = string.Empty;

        public string SourceFilePath { get; set; } = string.Empty;

        public string SurroundingContext { get; set; } = string.Empty;

        public double EntropyScore { get; set; }

        public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

        public DateTime? LastVerifiedAt { get; set; }
    }
}
