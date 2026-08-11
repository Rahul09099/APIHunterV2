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
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

        private static readonly Regex HostNamedRegex = new(
            @"\b(?:host|server|hostname|ip|endpoint)\b\s*[:=]\s*['""]?([a-zA-Z0-9.-]+|\[[a-fA-F0-9:]+\])['""]?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

        private static readonly Regex IPv4RawRegex = new(
            @"\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

        private static readonly Regex IPv6RawRegex = new(
            @"\b([a-fA-F0-9:]{3,}:[a-fA-F0-9:]+)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

        private static readonly Regex PortRegex = new(
            @"\bport\b\s*[:=]\s*['""]?(\d+)['""]?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

        private static readonly Regex GenericPasswordRegex = new(
            @"\b(?:db_|redis_|mysql_|postgres_|app_|site_)?(?:password|pass|pwd|secret|secret_key)\b\s*[:=]\s*(?:['""]([^'""]+)['""]|([^\s;,\n]+))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

        public ContextExtractor(ILogger<ContextExtractor>? logger = null)
        {
            _logger = logger;
        }
        
        public Task<CredentialContext> ExtractContextAsync(
            string fileContent,
            int matchPosition,
            int contextLines = 10)
        {
            if (string.IsNullOrEmpty(fileContent) || matchPosition < 0 || matchPosition > fileContent.Length)
            {
                return Task.FromResult(new CredentialContext
                {
                    FullContext = string.Empty,
                    MatchLine = 0,
                    StartLine = 0,
                    EndLine = 0
                });
            }

            var lines = fileContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var matchLine = GetLineNumber(fileContent, matchPosition);
            
            var startLine = Math.Max(0, matchLine - contextLines);
            var endLine = Math.Min(lines.Length - 1, matchLine + contextLines);
            
            var contextText = string.Join("\n", 
                lines.Skip(startLine).Take(endLine - startLine + 1));

            return Task.FromResult(new CredentialContext
            {
                FullContext = contextText,
                MatchLine = matchLine,
                StartLine = startLine,
                EndLine = endLine
            });
        }
        
        public string FindRelatedPassword(string context, string username)
        {
            if (string.IsNullOrEmpty(context)) return string.Empty;

            var centerPos = context.Length / 2;
            string? bestVal = null;
            int bestDistance = int.MaxValue;

            // 1. Username-specific pattern if username is non-empty
            if (!string.IsNullOrWhiteSpace(username))
            {
                try
                {
                    var userPattern = $@"\b{Regex.Escape(username)}\b['""]?\s*[:=]\s*(?:['""]([^'""]+)['""]|([^\s;,\n]+))";
                    var userMatches = Regex.Matches(context, userPattern, RegexOptions.IgnoreCase, RegexTimeout);
                    foreach (Match m in userMatches)
                    {
                        if (m.Success)
                        {
                            var val = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim('\'', '"', ';', ',');
                            if (!string.IsNullOrEmpty(val))
                            {
                                int dist = Math.Abs(m.Index - centerPos);
                                if (dist < bestDistance)
                                {
                                    bestDistance = dist;
                                    bestVal = val;
                                }
                            }
                        }
                    }

                    if (bestVal != null) return bestVal;
                }
                catch (RegexMatchTimeoutException ex)
                {
                    _logger?.LogWarning(ex, "Regex timeout searching for username-specific password in context");
                }
            }

            // 2. Generic password pattern — pick closest match to context center
            try
            {
                var matches = GenericPasswordRegex.Matches(context);
                foreach (Match m in matches)
                {
                    if (m.Success)
                    {
                        var val = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim('\'', '"', ';', ',');
                        if (!string.IsNullOrEmpty(val))
                        {
                            int dist = Math.Abs(m.Index - centerPos);
                            if (dist < bestDistance)
                            {
                                bestDistance = dist;
                                bestVal = val;
                            }
                        }
                    }
                }
            }
            catch (RegexMatchTimeoutException ex)
            {
                _logger?.LogWarning(ex, "Regex timeout searching for generic password in context");
            }
            
            return bestVal ?? string.Empty;
        }
        
        public string FindRelatedHost(string context)
        {
            if (string.IsNullOrEmpty(context)) return string.Empty;

            var centerPos = context.Length / 2;
            string? bestVal = null;
            int bestDistance = int.MaxValue;

            // 1. Try named host/server/hostname assignment first (highest confidence)
            try
            {
                var matches = HostNamedRegex.Matches(context);
                foreach (Match m in matches)
                {
                    if (m.Success)
                    {
                        var val = m.Groups[1].Value.Trim('\'', '"', ';', ',', '[', ']');
                        if (!string.IsNullOrEmpty(val))
                        {
                            int dist = Math.Abs(m.Index - centerPos);
                            if (dist < bestDistance)
                            {
                                bestDistance = dist;
                                bestVal = val;
                            }
                        }
                    }
                }

                if (bestVal != null) return bestVal;
            }
            catch (RegexMatchTimeoutException ex)
            {
                _logger?.LogWarning(ex, "Regex timeout searching for named host in context");
            }

            // 2. Fall back to raw IPv4/IPv6 address candidates — pick closest valid IP to center
            var rawRegexes = new[] { IPv4RawRegex, IPv6RawRegex };
            foreach (var regex in rawRegexes)
            {
                try
                {
                    var matches = regex.Matches(context);
                    foreach (Match m in matches)
                    {
                        if (m.Success)
                        {
                            var val = m.Groups[1].Value.Trim('\'', '"', ';', ',', '[', ']');
                            if (!string.IsNullOrEmpty(val) && System.Net.IPAddress.TryParse(val, out _))
                            {
                                int dist = Math.Abs(m.Index - centerPos);
                                if (dist < bestDistance)
                                {
                                    bestDistance = dist;
                                    bestVal = val;
                                }
                            }
                        }
                    }
                }
                catch (RegexMatchTimeoutException ex)
                {
                    _logger?.LogWarning(ex, "Regex timeout searching for raw IP host in context");
                }
            }

            return bestVal ?? string.Empty;
        }
        
        public int FindRelatedPort(string context, CredentialType type)
        {
            if (!string.IsNullOrEmpty(context))
            {
                var centerPos = context.Length / 2;
                int bestPort = 0;
                int bestDistance = int.MaxValue;

                try
                {
                    var matches = PortRegex.Matches(context);
                    foreach (Match m in matches)
                    {
                        if (m.Success && int.TryParse(m.Groups[1].Value, out var port) && port is >= 1 and <= 65535)
                        {
                            int dist = Math.Abs(m.Index - centerPos);
                            if (dist < bestDistance)
                            {
                                bestDistance = dist;
                                bestPort = port;
                            }
                        }
                    }

                    if (bestPort > 0) return bestPort;
                }
                catch (RegexMatchTimeoutException ex)
                {
                    _logger?.LogWarning(ex, "Regex timeout searching for port in context");
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
