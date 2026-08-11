using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Hugging Face User Access Tokens and OAuth tokens.
    /// Auth: Authorization: Bearer hf_...
    /// Verification endpoint: GET https://huggingface.co/api/whoami-v2
    /// Docs: https://huggingface.co/docs/hub/oauth
    /// </summary>
    [ApiProvider]
    public class HuggingFaceProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "HuggingFace";
        public override ApiTypeEnum ApiType => ApiTypeEnum.HuggingFace;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bhf_[A-Za-z0-9_-]{30,80}\b",
            @"HUGGINGFACE_API_KEY\s*[:=]\s*['""]?(hf_[A-Za-z0-9_-]{30,})['""]?",
            @"HF_TOKEN\s*[:=]\s*['""]?(hf_[A-Za-z0-9_-]{30,})['""]?",
            @"HUGGING_FACE_HUB_TOKEN\s*[:=]\s*['""]?(hf_[A-Za-z0-9_-]{30,})['""]?"
        ];

        public HuggingFaceProvider() : base() { }
        public HuggingFaceProvider(ILogger<HuggingFaceProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://huggingface.co/api/whoami-v2");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("HuggingFace whoami-v2 response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                ValidationResult result;

                if (response.IsSuccessStatusCode)
                {
                    result = ValidationResult.Success(response.StatusCode, "Valid HuggingFace token");
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true
                    };

                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("name", out var name))
                        {
                            string accName = name.GetString() ?? "";
                            result.AccountTier = accName;
                            result.Metadata["account_name"] = accName;
                        }

                        if (root.TryGetProperty("type", out var type))
                        {
                            result.Metadata["account_type"] = type.GetString() ?? "";
                        }

                        if (root.TryGetProperty("orgs", out var orgs) && orgs.ValueKind == JsonValueKind.Array)
                        {
                            result.Metadata["organization_count"] = orgs.GetArrayLength();
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger?.LogDebug(ex, "Failed to parse HuggingFace whoami-v2 response JSON.");
                    }

                    result.RawResponse = responseBody;
                    return result;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    result = ValidationResult.IsUnauthorized(
                        response.StatusCode,
                        "Invalid or expired HuggingFace token");
                    result.RawResponse = responseBody;
                    return result;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    result = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = "HuggingFace token was rejected with HTTP 403; token validity could not be determined."
                    };
                    result.RawResponse = responseBody;
                    return result;
                }

                if ((int)response.StatusCode == 429)
                {
                    result = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = "Hugging Face API rate limit exceeded (HTTP 429)"
                    };
                    result.RawResponse = responseBody;
                    return result;
                }

                result = ValidationResult.HasHttpError(response.StatusCode,
                    $"Status: {response.StatusCode} Body: {TruncateResponse(responseBody)}");
                result.RawResponse = responseBody;
                return result;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey) ||
                !apiKey.StartsWith("hf_", StringComparison.Ordinal))
            {
                return false;
            }

            var value = apiKey["hf_".Length..];
            return value.Length >= 27 &&
                   value.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');
        }
    }
}
