using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for AssemblyAI API keys — speech-to-text with advanced analysis.
    /// Verification: GET /v2/transcript?limit=1 (Authorization: {apiKey}, no Bearer prefix)
    /// Valid response: 200 with { "transcripts": [...], "page_details": {...} }
    /// Invalid response: 401 with { "error": "Authentication error, API token missing/invalid" }
    /// No balance endpoint available via API — usage tracked in dashboard only.
    /// </summary>
    [ApiProvider]
    public class AssemblyAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "AssemblyAI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.AssemblyAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"[a-f0-9]{32}",                         // AssemblyAI uses 32-char hex tokens
            @"assemblyai[_-]?[A-Za-z0-9]{20,}",
            @"ASSEMBLYAI_API_KEY",
            @"ASSEMBLY_AI_API_KEY"
        ];

        public AssemblyAIProvider() : base() { }
        public AssemblyAIProvider(ILogger<AssemblyAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // AssemblyAI uses plain "Authorization: <key>" (no Bearer prefix)
                // GET /v2/transcript?limit=1 is the lightest read-only endpoint
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.assemblyai.com/v2/transcript?limit=1");
                request.Headers.Add("Authorization", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("AssemblyAI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid AssemblyAI key");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        // Response: { "transcripts": [...], "page_details": { "result_count": N } }
                        if (doc.RootElement.TryGetProperty("page_details", out var pageDetails) &&
                            pageDetails.TryGetProperty("result_count", out var count))
                        {
                            result.Detail = $"Valid AssemblyAI key — {count.GetInt32()} transcript(s) on account";
                        }
                        else
                        {
                            result.Detail = "Valid AssemblyAI key";
                        }
                        // No balance endpoint available via API
                        result.Balance = "N/A (check dashboard)";
                    }
                    catch { /* Best effort */ }

                    return result;
                }

                return response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or
                    System.Net.HttpStatusCode.Forbidden =>
                        ValidationResult.IsUnauthorized(response.StatusCode),
                    (System.Net.HttpStatusCode)429 =>
                        ValidationResult.Success(response.StatusCode, "Rate limited (key is valid)"),
                    _ => ValidationResult.HasHttpError(response.StatusCode,
                        $"Unexpected status {response.StatusCode}. Body: {TruncateResponse(responseBody)}")
                };
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 32;
        }
    }
}
