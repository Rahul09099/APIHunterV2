using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Models;

namespace UnsecuredAPIKeys.Providers.ServerProviders.Services
{
    public interface IContextExtractor
    {
        Task<CredentialContext> ExtractContextAsync(
            string fileContent,
            int matchPosition,
            int contextLines = 10);
        
        string FindRelatedPassword(string context, string username);
        string FindRelatedHost(string context);
        int FindRelatedPort(string context, CredentialType type);
    }

    public class ContextExtractor : IContextExtractor
    {
        private readonly ILogger<ContextExtractor>? _logger;

        public ContextExtractor(ILogger<ContextExtractor>? logger = null)
        {
            _logger = logger;
        }
        
        public async Task<CredentialContext> ExtractContextAsync(
            string fileContent,
            int matchPosition,
            int contextLines = 10)
        {
            if (string.IsNullOrEmpty(fileContent))
            {
                return new CredentialContext
                {
                    FullContext = string.Empty,
                    MatchLine = 0,
                    StartLine = 0,
                    EndLine = 0
                };
            }

            var lines = fileContent.Split('\n');
            var matchLine = GetLineNumber(fileContent, matchPosition);
            
            var startLine = Math.Max(0, matchLine - contextLines);
            var endLine = Math.Min(lines.Length - 1, matchLine + contextLines);
            
            var contextText = string.Join("\n", 
                lines.Skip(startLine).Take(endLine - startLine + 1));
            
            await Task.CompletedTask;

            return new CredentialContext
            {
                FullContext = contextText,
                MatchLine = matchLine,
                StartLine = startLine,
                EndLine = endLine
            };
        }
        
        public string FindRelatedPassword(string context, string username)
        {
            if (string.IsNullOrEmpty(context)) return string.Empty;

            // Search for password patterns near username
            var patterns = new[]
            {
                $@"{Regex.Escape(username)}['""]?\s*[:=]\s*['""]?([^\s'""\n]+)",
                @"password\s*[:=]\s*['""]?([^\s'""\n]+)['""]?",
                @"pass\s*[:=]\s*['""]?([^\s'""\n]+)['""]?",
                @"pwd\s*[:=]\s*['""]?([^\s'""\n]+)['""]?",
            };
            
            foreach (var pattern in patterns)
            {
                var match = Regex.Match(context, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var val = match.Groups[1].Value.Trim('\'', '"', ';', ',');
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
            
            return string.Empty;
        }
        
        public string FindRelatedHost(string context)
        {
            if (string.IsNullOrEmpty(context)) return string.Empty;

            var patterns = new[]
            {
                @"host\s*[:=]\s*['""]?([a-zA-Z0-9.-]+)['""]?",
                @"server\s*[:=]\s*['""]?([a-zA-Z0-9.-]+)['""]?",
                @"hostname\s*[:=]\s*['""]?([a-zA-Z0-9.-]+)['""]?",
                @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})",
            };
            
            foreach (var pattern in patterns)
            {
                var match = Regex.Match(context, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var val = match.Groups[1].Value.Trim('\'', '"', ';', ',');
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
            
            return string.Empty;
        }
        
        public int FindRelatedPort(string context, CredentialType type)
        {
            if (!string.IsNullOrEmpty(context))
            {
                var portPattern = @"port\s*[:=]\s*['""]?(\d+)['""]?";
                var match = Regex.Match(context, portPattern, RegexOptions.IgnoreCase);
                
                if (match.Success && int.TryParse(match.Groups[1].Value, out var port))
                {
                    return port;
                }
            }
            
            // Return default port for credential type
            return GetDefaultPort(type);
        }
        
        private int GetLineNumber(string content, int position)
        {
            if (position <= 0) return 0;
            if (position > content.Length) position = content.Length;
            return content.Substring(0, position).Count(c => c == '\n');
        }

        private int GetDefaultPort(CredentialType type)
        {
            return type switch
            {
                CredentialType.SSH => 22,
                CredentialType.FTP => 21,
                CredentialType.SFTP => 22,
                CredentialType.RDP => 3389,
                CredentialType.VNC => 5900,
                CredentialType.WinRM_HTTP => 5985,
                CredentialType.WinRM_HTTPS => 5986,
                CredentialType.SMTP => 25,
                CredentialType.SMTP_Submission => 587,
                CredentialType.SMTPS => 465,
                CredentialType.IMAP => 143,
                CredentialType.IMAPS => 993,
                CredentialType.POP3 => 110,
                CredentialType.POP3S => 995,
                CredentialType.MySQL => 3306,
                CredentialType.PostgreSQL => 5432,
                CredentialType.MongoDB => 27017,
                CredentialType.Redis => 6379,
                CredentialType.MSSQL => 1433,
                CredentialType.Kubernetes => 6443,
                CredentialType.Docker => 2375,
                CredentialType.cPanel_HTTP => 2082,
                CredentialType.cPanel_HTTPS => 2083,
                CredentialType.WHM_HTTP => 2086,
                CredentialType.WHM_HTTPS => 2087,
                CredentialType.Plesk => 8443,
                _ => 0
            };
        }
    }
}
