using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for AssemblyAI API keys — speech-to-text with advanced audio intelligence.
    ///
    /// Key format: plain alphanumeric string, NO fixed prefix.
    ///   - GitGuardian confirms: Prefixed=False, High recall=False
    ///   - Keys are alphanumeric (a-z, A-Z, 0-9), NOT hex-only
    ///   - Typical length: 32 characters
    ///   - Official docs use placeholder: &lt;YOUR_API_KEY&gt;
    ///
    /// Auth: Authorization: {apiKey}   (plain header — NO "Bearer" prefix)
    ///   Confirmed from official docs: headers = {"authorization": "&lt;YOUR_API_KEY&gt;"}
    ///
    /// Verification: GET https://api.assemblyai.com/v2/transcript?limit=1
    ///   - User-specific endpoint — 401 without valid key
    ///   - Valid response: 200 { "transcripts": [...], "page_details": { "result_count": N, ... } }
    ///   - Invalid response: 401 { "error": "Authentication error, API token missing/invalid" }
    ///
    /// No balance/credits endpoint available via API — usage tracked in dashboard only.
    /// </summary>
    [ApiProvider]
    public class AssemblyAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "AssemblyAI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.AssemblyAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Primary env var names — confirmed from official docs and security research
            @"ASSEMBLYAI_API_KEY",
            @"ASSEMBLY_AI_API_KEY",
            @"ASSEMBLYAI_KEY",
            @"ASSEMBLY_API_KEY",

            // Context-aware value extraction patterns
            // Keys are plain alphanumeric, no fixed prefix (GitGuardian: Prefixed=False)
            @"ASSEMBLYAI_API_KEY\s*[=:]\s*['""]?([A-Za-z0-9]{32,})['""]?",
            @"ASSEMBLY_AI_API_KEY\s*[=:]\s*['""]?([A-Za-z0-9]{32,})['""]?",
            @"assemblyai[._-]?api[._-]?key\s*[=:]\s*['""]?([A-Za-z0-9]{32,})['""]?"
        ];

        public AssemblyAIProvider() : base() { }
        public AssemblyAIProvider(ILogger<AssemblyAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // GET /v2/transcript?limit=1 — user-specific, always requires auth
                // Confirmed from official docs: headers = {"authorization": "<YOUR_API_KEY>"}
                // Plain Authorization header — NO "Bearer" prefix
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.assemblyai.com/v2/transcript?limit=1");
                request.Headers.Add("Authorization", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("AssemblyAI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                ValidationResult result;

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    result = ValidationResult.Success(response.StatusCode, "Valid AssemblyAI key");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        var root = doc.RootElement;
                        var metadata = new Dictionary<string, object>();

                        if (root.TryGetProperty("page_details", out var pageDetails) &&
                            pageDetails.TryGetProperty("result_count", out var count))
                        {
                            int transcriptCount = count.GetInt32();
                            metadata["transcript_count"] = transcriptCount;
                            result.Detail = transcriptCount > 0
                                ? $"Valid AssemblyAI key — {transcriptCount} transcript(s) on account"
                                : "Valid AssemblyAI key — no transcripts yet";
                        }
                        else
                        {
                            result.Detail = "Valid AssemblyAI key";
                        }

                        if (root.TryGetProperty("transcripts", out var transcripts) &&
                            transcripts.ValueKind == System.Text.Json.JsonValueKind.Array &&
                            transcripts.GetArrayLength() > 0)
                        {
                            var first = transcripts[0];
                            if (first.TryGetProperty("status", out var st))
                            {
                                metadata["latest_transcript_status"] = st.GetString() ?? "unknown";
                            }
                            if (first.TryGetProperty("error", out var err) && err.ValueKind != System.Text.Json.JsonValueKind.Null)
                            {
                                metadata["latest_transcript_error"] = err.GetString() ?? string.Empty;
                            }
                        }

                        result.Metadata = metadata;
                        result.Balance = "N/A (check assemblyai.com dashboard)";
                    }
                    catch { result.Detail = "Valid AssemblyAI key"; }
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                         responseBody.Contains("Authentication error") ||
                         responseBody.Contains("API token missing"))
                {
                    result = ValidationResult.IsUnauthorized(response.StatusCode, "Invalid AssemblyAI API key");
                }
                else if ((int)response.StatusCode == 429)
                {
                    result = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = "AssemblyAI rate limit exceeded (HTTP 429)"
                    };
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    result = ValidationResult.IsUnauthorized(response.StatusCode, "AssemblyAI key access forbidden");
                }
                else
                {
                    result = ValidationResult.HasHttpError(response.StatusCode,
                        $"Unexpected status {response.StatusCode}. Body: {TruncateResponse(responseBody)}");
                }

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
            // AssemblyAI keys: plain alphanumeric, no fixed prefix, typically 32 chars
            // GitGuardian: Prefixed=False — do NOT enforce any prefix
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.Length >= 32 &&
                   System.Text.RegularExpressions.Regex.IsMatch(apiKey, @"^[A-Za-z0-9]+$");
        }
    }
}
