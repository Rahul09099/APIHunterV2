using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for RunPod API keys — cloud GPU marketplace and serverless endpoints.
    ///
    /// Key format: rpa_{alphanumeric} (e.g. rpa_TG1IX3V0HB2KN69ZKJE0W6104CNEV21HW2T9ZMII3gsa20)
    /// Confirmed from RunPod's own blog example: https://www.runpod.io/blog/runpod-rest-api-gpu-management
    ///
    /// Two APIs available:
    ///   REST API (new): https://rest.runpod.io/v1  — Authorization: Bearer {key}
    ///   GraphQL (legacy): https://api.runpod.io/graphql?api_key={key}
    ///
    /// Verification strategy:
    ///   1. Try REST API first: GET https://rest.runpod.io/v1/pods
    ///      Response: array of pod objects (may be empty [] for new accounts — still valid)
    ///   2. Fallback to GraphQL: POST https://api.runpod.io/graphql?api_key={key}
    ///      Query: { myself { id email currentSpendPerHr spendLimit } }
    ///      Response: { "data": { "myself": { "id": "...", "email": "...", ... } } }
    ///      Invalid key: { "errors": [{ "message": "..." }] } with no "myself" data
    ///
    /// Balance: currentSpendPerHr from GraphQL myself query
    /// </summary>
    [ApiProvider]
    public class RunPodProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "RunPod";
        public override ApiTypeEnum ApiType => ApiTypeEnum.RunPod;

        public override IEnumerable<string> RegexPatterns =>
        [
            // Confirmed key format from RunPod's own documentation and blog
            @"rpa_[A-Za-z0-9]{40,}",

            // Environment variable names commonly found in leaked code
            @"RUNPOD_API_KEY",
            @"RUNPOD_API_SECRET",
            @"RUNPOD_TOKEN"
        ];

        public RunPodProvider() : base() { }
        public RunPodProvider(ILogger<RunPodProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            // Strategy: Try REST API first (newer, cleaner), fall back to GraphQL
            var restResult = await TryRestApiAsync(apiKey, httpClient);
            if (restResult != null)
                return restResult;

            // Fallback to GraphQL
            return await TryGraphQLAsync(apiKey, httpClient);
        }

        private async Task<ValidationResult?> TryRestApiAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // GET /v1/pods — lightweight read-only list, returns [] for new accounts (still valid)
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://rest.runpod.io/v1/pods");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("RunPod REST API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid RunPod key");

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        // Response is an array of pod objects
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var podCount = doc.RootElement.GetArrayLength();
                            result.Detail = podCount > 0
                                ? $"Valid RunPod key — {podCount} active pod(s)"
                                : "Valid RunPod key — no active pods";
                        }
                    }
                    catch { /* Best effort */ }

                    return result;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }

                // For other errors, fall through to GraphQL fallback
                return null;
            }
            catch
            {
                // Network error on REST — try GraphQL fallback
                return null;
            }
        }

        private async Task<ValidationResult> TryGraphQLAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // GraphQL query — api_key as query param (confirmed from official docs)
                const string query = """{"query": "{ myself { id email currentSpendPerHr spendLimit } }"}""";

                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"https://api.runpod.io/graphql?api_key={Uri.EscapeDataString(apiKey)}");
                request.Content = new StringContent(query,
                    System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("RunPod GraphQL response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (!IsSuccessStatusCode(response.StatusCode))
                {
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

                // GraphQL always returns 200 — must check body for errors
                // Invalid key: { "errors": [...] } with no "myself" field
                if (responseBody.Contains("\"errors\"") &&
                    !responseBody.Contains("\"myself\""))
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode,
                        "Invalid RunPod API key — GraphQL returned auth error");
                }

                var result = ValidationResult.Success(response.StatusCode, "Valid RunPod key");

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("myself", out var myself))
                    {
                        // Email as account identifier
                        if (myself.TryGetProperty("email", out var email))
                            result.AccountTier = email.GetString();

                        // currentSpendPerHr = active spend rate
                        if (myself.TryGetProperty("currentSpendPerHr", out var spend))
                            result.Balance = $"${spend.GetDouble():N4}/hr";

                        // spendLimit = account spend cap (null = unlimited)
                        if (myself.TryGetProperty("spendLimit", out var limit) &&
                            limit.ValueKind != System.Text.Json.JsonValueKind.Null)
                            result.Detail = $"Valid RunPod key — spend limit: ${limit.GetDouble():N2}";
                        else
                            result.Detail = "Valid RunPod key — no spend limit";
                    }
                }
                catch { /* Best effort */ }

                return result;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            // RunPod keys start with rpa_ and are ~44+ chars total
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.StartsWith("rpa_") &&
                   apiKey.Length >= 44;
        }
    }
}
