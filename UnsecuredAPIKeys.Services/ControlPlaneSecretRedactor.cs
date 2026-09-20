using System.Text.RegularExpressions;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Last-resort textual redaction for control-plane diagnostics. Structured logging
/// must still avoid supplying secrets in the first place.
/// </summary>
public sealed partial class ControlPlaneSecretRedactor
{
    private const string Replacement = "[REDACTED]";

    public string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var redacted = AuthorizationHeaderRegex().Replace(input, "$1" + Replacement);
        redacted = SensitiveHeaderRegex().Replace(redacted, "$1" + Replacement);
        redacted = NamedCredentialRegex().Replace(redacted, "$1" + Replacement);
        redacted = FullFingerprintRegex().Replace(redacted, "$1" + Replacement);
        redacted = KnownProviderCredentialRegex().Replace(redacted, Replacement);
        return redacted;
    }

    public string Sanitize(string input) => Redact(input);

    [GeneratedRegex(@"(?i)(\bAuthorization\s*:\s*(?:Bearer\s+)?)[^;\r\n,]+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex(@"(?i)(\b(?:X-Node-Token|PRIVATE-TOKEN)\s*:\s*)[^;\r\n,]+", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveHeaderRegex();

    [GeneratedRegex(@"(?i)(\b(?:credential|providerToken|nodeToken|secret|token)\s*=\s*)[^;\s,]+", RegexOptions.CultureInvariant)]
    private static partial Regex NamedCredentialRegex();

    [GeneratedRegex(@"(?i)(\b(?:full)?fingerprint\s*=\s*)[a-f0-9]{64}\b", RegexOptions.CultureInvariant)]
    private static partial Regex FullFingerprintRegex();

    [GeneratedRegex(@"(?i)\b(?:ghp_|github_pat_|glpat-|task-4\.1-provider-credential-)[^\s;,]+", RegexOptions.CultureInvariant)]
    private static partial Regex KnownProviderCredentialRegex();
}
