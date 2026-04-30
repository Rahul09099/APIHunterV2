using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for RunPod API keys — cloud GPU marketplace and serverless endpoints.
    /// Verification endpoint: POST https://api.runpod.io/graphql (GraphQL introspection with Bearer auth)
    /// </summary>
    [ApiProvider(false, false)]
    public class RunPodProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "RunPod";
        public override ApiTypeEnum ApiType => ApiTypeEnum.RunPod;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"[A-Za-z0-9]{32}[A-Za-z0-9]*",        // RunPod uses alphanumeric API keys
            @"runpod[_-]?[A-Za-z0-9]{20,}",
            @"RUNPOD_API_KEY"
        ];

        public RunPodProvider() : base() { }
        public RunPodProvider(ILogger<RunPodProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // RunPod GraphQL — api_key as query param (confirmed from docs)
                // Also add Bearer header as fallback for scoped keys
                const string query = """{"query": "{ myself { id email currentSpendPerHr spendLimit } }"}""";

                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"https://api.runpod.io/graphql?api_key={Uri.EscapeDataString(apiKey)}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(query,
                    System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("RunPod API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    // GraphQL always returns 200; check for errors in body
                    if (responseBody.Contains("\"errors\"") && !responseBody.Contains("\"myself\""))
                    {
                        return ValidationResult.IsUnauthorized(response.StatusCode,
                            "GraphQL returned errors — key may be invalid");
                    }

                    var result = ValidationResult.Success(response.StatusCode, "Valid RunPod key");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("data", out var data) &&
                            data.TryGetProperty("myself", out var myself))
                        {
                            if (myself.TryGetProperty("email", out var email))
                                result.AccountTier = email.GetString();

                            if (myself.TryGetProperty("currentSpendPerHr", out var spend))
                                result.Balance = $"${spend.GetDouble():N4}/hr current spend";

                            if (myself.TryGetProperty("spendLimit", out var limit) &&
                                limit.ValueKind != System.Text.Json.JsonValueKind.Null)
                                result.Detail = $"Valid RunPod key — spend limit: ${limit.GetDouble():N2}";
                            else
                                result.Detail = "Valid RunPod key — no spend limit set";
                        }
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
