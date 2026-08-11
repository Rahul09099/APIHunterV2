using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.Communication_Providers
{
    /// <summary>
    /// Provider for Google Cloud Storage HMAC Access Key & Secret Candidates
    /// </summary>
    [ApiProvider]
    public class GcpHmacProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Google Cloud HMAC";
        public override ApiTypeEnum ApiType => ApiTypeEnum.GcpHmac;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bGOOG[A-Z0-9]{20}\b|\bGOOG[A-Z0-9]{57}\b",
            @"GOOGLE_CLOUD_HMAC_ACCESS_KEY_ID\s*=\s*['""]?([A-Za-z0-9]+)['""]?",
            @"GOOGLE_CLOUD_HMAC_SECRET_ACCESS_KEY\s*=\s*['""]?([A-Za-z0-9+/=]{40})['""]?"
        ];

        public GcpHmacProvider() : base() { }
        public GcpHmacProvider(ILogger<GcpHmacProvider>? logger) : base(logger) { }

        protected override Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Secret candidate check (exact 40-character Base64 string)
                if (LooksLikeHmacSecret(apiKey))
                {
                    return Task.FromResult(new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Candidate,
                        AccountTier = "HMAC Secret Candidate",
                        Detail = "Google Cloud Storage HMAC secret candidate detected; requires the corresponding HMAC Access ID for signed authentication.",
                        Metadata = new Dictionary<string, object>
                        {
                            ["CredentialType"] = "GCP Storage HMAC Secret",
                            ["IsSecret"] = true,
                            ["RequiresAccessId"] = true
                        }
                    });
                }

                // Access ID candidate check (24-character user ID or 61-character service account ID starting with GOOG)
                if (LooksLikeHmacAccessId(apiKey))
                {
                    return Task.FromResult(new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Candidate,
                        AccountTier = "HMAC Access ID",
                        Detail = "Google Cloud Storage HMAC access key ID candidate detected; live validation requires the corresponding HMAC secret.",
                        Metadata = new Dictionary<string, object>
                        {
                            ["CredentialType"] = "GCP Storage HMAC Access ID",
                            ["RequiresSecret"] = true
                        }
                    });
                }

                // Generic GCP HMAC Credential Candidate
                return Task.FromResult(new ValidationResult
                {
                    Status = ValidationAttemptStatus.Candidate,
                    AccountTier = "HMAC Credential Candidate",
                    Detail = "Google Cloud Storage HMAC candidate detected.",
                    Metadata = new Dictionary<string, object>
                    {
                        ["CredentialType"] = "GCP Storage HMAC Candidate",
                        ["RequiresSecret"] = true
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to process GCP HMAC credential");
                return Task.FromResult(ValidationResult.HasProviderSpecificError("GCP HMAC processing failed."));
            }
        }

        private static bool LooksLikeHmacSecret(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 40)
                return false;

            try
            {
                Convert.FromBase64String(value);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool LooksLikeHmacAccessId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return (value.Length == 24 || value.Length == 61) &&
                   value.StartsWith("GOOG", StringComparison.Ordinal) &&
                   value.All(char.IsLetterOrDigit);
        }
    }
}
