using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider implementation for validating Tavily AI Search API keys via Tavily's official usage endpoint:
    /// GET https://api.tavily.com/usage
    /// Headers: Authorization: Bearer <token>
    /// Extracts exact account plan and key usage metrics:
    /// - account.current_plan (e.g. Researcher, Bootstrap, Startup, Growth, Pay-as-you-go, Enterprise)
    /// - account.plan_usage / account.plan_limit
    /// - key.usage / key.limit
    /// - Detailed usage breakdowns (search, extract, crawl, map, research, paygo)
    /// </summary>
    [ApiProvider]
    public class TavilyProvider : BaseApiKeyProvider
    {
        private const string USAGE_ENDPOINT = "https://api.tavily.com/usage";

        public override string ProviderName => "Tavily";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Tavily;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\btvly-[a-zA-Z0-9_-]{25,60}\b",
            @"TAVILY[_-]?KEY",
            @"TAVILY[_-]?API[_-]?KEY"
        ];

        public TavilyProvider() : base() { }
        public TavilyProvider(ILogger<TavilyProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, USAGE_ENDPOINT);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var response = await httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Tavily usage response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(content));

                // 200 OK: Valid Tavily key with full account usage object
                if (response.IsSuccessStatusCode)
                {
                    string currentPlan = "Researcher";
                    long planUsage = 0, planLimit = 0, paygoUsage = 0, paygoLimit = 0;
                    long keyUsage = 0, keyLimit = 0;
                    long searchUsage = 0, extractUsage = 0, crawlUsage = 0, mapUsage = 0, researchUsage = 0;

                    try
                    {
                        using var doc = JsonDocument.Parse(content);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("account", out var accountObj))
                        {
                            if (accountObj.TryGetProperty("current_plan", out var planProp))
                            {
                                currentPlan = planProp.GetString() ?? "Researcher";
                            }
                            if (accountObj.TryGetProperty("plan_usage", out var puProp)) planUsage = puProp.GetInt64();
                            if (accountObj.TryGetProperty("plan_limit", out var plProp)) planLimit = plProp.GetInt64();
                            if (accountObj.TryGetProperty("paygo_usage", out var pguProp)) paygoUsage = pguProp.GetInt64();
                            if (accountObj.TryGetProperty("paygo_limit", out var pglProp)) paygoLimit = pglProp.GetInt64();
                        }

                        if (root.TryGetProperty("key", out var keyObj))
                        {
                            if (keyObj.TryGetProperty("usage", out var kuProp)) keyUsage = kuProp.GetInt64();
                            if (keyObj.TryGetProperty("limit", out var klProp)) keyLimit = klProp.GetInt64();
                            if (keyObj.TryGetProperty("search_usage", out var suProp)) searchUsage = suProp.GetInt64();
                            if (keyObj.TryGetProperty("extract_usage", out var euProp)) extractUsage = euProp.GetInt64();
                            if (keyObj.TryGetProperty("crawl_usage", out var cuProp)) crawlUsage = cuProp.GetInt64();
                            if (keyObj.TryGetProperty("map_usage", out var muProp)) mapUsage = muProp.GetInt64();
                            if (keyObj.TryGetProperty("research_usage", out var ruProp)) researchUsage = ruProp.GetInt64();
                        }
                    }
                    catch (JsonException)
                    {
                        // Fallback if parsing encounters unexpected JSON schema
                    }

                    bool quotaExceeded = planLimit > 0 && planUsage >= planLimit;

                    var metadata = new Dictionary<string, object>
                    {
                        ["CurrentPlan"] = currentPlan,
                        ["PlanUsage"] = planUsage,
                        ["PlanLimit"] = planLimit,
                        ["PaygoUsage"] = paygoUsage,
                        ["PaygoLimit"] = paygoLimit,
                        ["KeyUsage"] = keyUsage,
                        ["KeyLimit"] = keyLimit,
                        ["SearchUsage"] = searchUsage,
                        ["ExtractUsage"] = extractUsage,
                        ["CrawlUsage"] = crawlUsage,
                        ["MapUsage"] = mapUsage,
                        ["ResearchUsage"] = researchUsage
                    };

                    if (quotaExceeded)
                    {
                        return new ValidationResult
                        {
                            Status = ValidationAttemptStatus.Valid,
                            HttpStatusCode = response.StatusCode,
                            IsQuotaExceeded = true,
                            AccountTier = currentPlan,
                            Detail = $"Valid Tavily key ({currentPlan} Plan) but monthly limit reached ({planUsage}/{planLimit})",
                            Metadata = metadata,
                            RawResponse = content
                        };
                    }

                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Valid,
                        HttpStatusCode = response.StatusCode,
                        AccountTier = currentPlan,
                        Detail = $"Valid Tavily key ({currentPlan} Plan - Usage: {planUsage}/{planLimit})",
                        Metadata = metadata,
                        RawResponse = content
                    };
                }

                var contentLower = content.ToLowerInvariant();

                // 429 / Quota / Limit exceeded errors
                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    response.StatusCode == HttpStatusCode.PaymentRequired ||
                    contentLower.Contains("quota") ||
                    contentLower.Contains("limit") ||
                    contentLower.Contains("exceeded"))
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Valid,
                        HttpStatusCode = response.StatusCode,
                        IsQuotaExceeded = true,
                        Detail = $"Valid key but limit / quota exceeded: {TruncateResponse(content)}"
                    };
                }

                // 401 Unauthorized / Invalid key errors
                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden ||
                    contentLower.Contains("invalid") ||
                    contentLower.Contains("unauthorized"))
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode, "Invalid Tavily API key.");
                }

                return ValidationResult.HasHttpError(response.StatusCode, $"API Error: {TruncateResponse(content)}");
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return false;
            var cleanKey = CleanApiKey(apiKey);
            return cleanKey.StartsWith("tvly-", StringComparison.OrdinalIgnoreCase) && cleanKey.Length >= 25;
        }
    }
}
