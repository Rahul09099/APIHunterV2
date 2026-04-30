using System.Net;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{

    [ApiProvider]
    public class CohereProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Cohere";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Cohere;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"[A-Za-z0-9]{40}",  // Cohere uses 40-char tokens
            @"cohere[_-]?[A-Za-z0-9]{32,}",
            @"COHERE_API_KEY",
            @"CO_API_KEY"
        ];

        public CohereProvider() : base() { }
        public CohereProvider(ILogger<CohereProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // 1. Get account IDs using v1 check-api-key
                string orgId = null;
                string ownerId = null;
                
                using var checkRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.cohere.com/v1/check-api-key");
                checkRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                
                var checkResponse = await httpClient.SendAsync(checkRequest);
                if (checkResponse.StatusCode == HttpStatusCode.Unauthorized || checkResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(checkResponse.StatusCode);
                }

                if (IsSuccessStatusCode(checkResponse.StatusCode))
                {
                    var checkBody = await checkResponse.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(checkBody);
                    if (doc.RootElement.TryGetProperty("organization_id", out var orgProp)) orgId = orgProp.GetString();
                    if (doc.RootElement.TryGetProperty("owner_id", out var ownerProp)) ownerId = ownerProp.GetString();
                }

                // 2. Check chat capability and extract limits from headers
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.cohere.com/v2/chat");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                var requestBody = new
                {
                    model = "command-r-08-2024",
                    messages = new[] { new { role = "user", content = "hi" } },
                    max_tokens = 1
                };

                var jsonContent = System.Text.Json.JsonSerializer.Serialize(requestBody);
                request.Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Cohere API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                // Extract metadata from headers
                string remaining = response.Headers.Contains("x-trial-endpoint-call-remaining") 
                    ? response.Headers.GetValues("x-trial-endpoint-call-remaining").FirstOrDefault() 
                    : null;
                string limit = response.Headers.Contains("x-endpoint-monthly-call-limit") 
                    ? response.Headers.GetValues("x-endpoint-monthly-call-limit").FirstOrDefault() 
                    : null;
                bool isTrial = response.Headers.Contains("x-trial-endpoint-call-limit") || responseBody.Contains("Trial key");

                var detailStr = $"Org: {orgId}, Owner: {ownerId}";
                var tierStr = isTrial ? $"Trial (Limit: {limit})" : $"Paid (Limit: {limit})";

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Key is valid.");
                    result.AccountTier = tierStr;
                    result.Detail = string.IsNullOrEmpty(remaining) ? detailStr : $"{remaining} calls remaining. {detailStr}";
                    return result;
                }
                else
                {
                    var result = ValidationResult.Success(response.StatusCode, $"Valid key but access issue: {TruncateResponse(responseBody)}");
                    result.AccountTier = tierStr;

                    if (response.StatusCode == (HttpStatusCode)429 || ContainsAny(responseBody, new HashSet<string> { "quota", "billing", "limit", "insufficient", "trial" }))
                    {
                        result.IsQuotaExceeded = true;
                        result.Detail = $"Quota reached. {detailStr}";
                    }
                    else
                    {
                        return ValidationResult.HasHttpError(response.StatusCode, 
                            $"API request failed with status {response.StatusCode}. {detailStr}");
                    }

                    return result;
                }
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
