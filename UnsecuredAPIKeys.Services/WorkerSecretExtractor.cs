using System.Text.RegularExpressions;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

/// <summary>One candidate secret extracted from provider content on the Worker.</summary>
public sealed record WorkerSecretFinding(ApiTypeEnum ApiType, string ApiKey);

/// <summary>
/// Worker-side secret detection (Wave 13). Compiles the same provider patterns as the
/// Master scraper from <see cref="ApiProviderRegistry.ScraperProviders"/> so findings
/// classify identically; only transport and scheduling differ. Stateless and DB-free.
/// </summary>
public sealed class WorkerSecretExtractor
{
    private readonly IReadOnlyList<(IApiKeyProvider Provider, Regex Regex)> _compiledPatterns;

    public WorkerSecretExtractor()
        : this(ApiProviderRegistry.ScraperProviders)
    {
    }

    public WorkerSecretExtractor(IReadOnlyList<IApiKeyProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var compiled = new List<(IApiKeyProvider, Regex)>();
        foreach (var provider in providers)
        {
            foreach (var pattern in provider.RegexPatterns)
            {
                try
                {
                    compiled.Add((provider, new Regex(
                        pattern,
                        RegexOptions.Compiled | RegexOptions.IgnoreCase,
                        TimeSpan.FromSeconds(2))));
                }
                catch
                {
                    // An invalid pattern disables one provider signal, never the cycle.
                }
            }
        }

        _compiledPatterns = compiled;
    }

    public IReadOnlyList<WorkerSecretFinding> ExtractSecrets(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var findings = new List<WorkerSecretFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (provider, regex) in _compiledPatterns)
        {
            MatchCollection matches;
            try
            {
                matches = regex.Matches(content);
            }
            catch
            {
                continue;
            }

            foreach (var match in matches.Cast<Match>())
            {
                var value = match.Value.Trim();
                if (value.Length == 0 || !seen.Add(value))
                {
                    continue;
                }

                findings.Add(new WorkerSecretFinding(provider.ApiType, value));
            }
        }

        return findings;
    }
}
