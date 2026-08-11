using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for RunPod API keys — cloud GPU marketplace and serverless endpoints.
    ///
    /// Key format: rpa_{alphanumeric} (e.g. rpa_TG1IX3V0HB2KN69ZKJE0W6104CNEV21HW2T9ZMII3gsa20)
    /// Confirmed from RunPod's official documentation.
    ///
    /// Verification strategy:
    ///   1. REST API: GET https://rest.runpod.io/v1/pods — Authorization: Bearer {key} (active pod count)
    ///   2. GraphQL: POST https://api.runpod.io/graphql (account state + read-only real-time GPU stock capacity)
    ///      Query: { myself { id email clientBalance currentSpendPerHr underBalance minBalance creditAlertThreshold }
    ///               gpuTypes { id displayName memoryInGb secureCloud communityCloud lowestPrice { minimumBidPrice uninterruptablePrice stockStatus availableGpuCounts } } }
    /// </summary>
    [ApiProvider]
    public class RunPodProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "RunPod";
        public override ApiTypeEnum ApiType => ApiTypeEnum.RunPod;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\brpa_[A-Za-z0-9]{40,}\b",
            @"RUNPOD_API_KEY",
            @"RUNPOD_API_SECRET",
            @"RUNPOD_TOKEN"
        ];

        public RunPodProvider() : base() { }
        public RunPodProvider(ILogger<RunPodProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            int? activePodCount = null;
            try
            {
                using var restRequest = new HttpRequestMessage(HttpMethod.Get, "https://rest.runpod.io/v1/pods");
                restRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var restResponse = await httpClient.SendAsync(restRequest);
                if (IsSuccessStatusCode(restResponse.StatusCode))
                {
                    string restBody = await restResponse.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(restBody);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        activePodCount = doc.RootElement.GetArrayLength();
                    }
                }
                else if (restResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(restResponse.StatusCode,
                        "Invalid or expired RunPod API key (REST API)");
                }
            }
            catch { /* REST check is best-effort fallback for active pods */ }

            return await TryGraphQLAsync(apiKey, httpClient, activePodCount);
        }

        private async Task<ValidationResult> TryGraphQLAsync(string apiKey, HttpClient httpClient, int? activePodCount)
        {
            try
            {
                const string query = """{"query": "query { myself { id email clientBalance currentSpendPerHr underBalance minBalance creditAlertThreshold } gpuTypes { id displayName memoryInGb secureCloud communityCloud lowestPrice { minimumBidPrice uninterruptablePrice stockStatus availableGpuCounts } } }"}""";

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.runpod.io/graphql");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(query, System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("RunPod GraphQL response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode,
                        "Invalid or expired RunPod API key (GraphQL)");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        "RunPod GraphQL access forbidden (403)");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        "RunPod GraphQL rate limited (429)");
                }

                if ((int)response.StatusCode >= 500)
                {
                    return ValidationResult.ValidationUnavailable(response.StatusCode,
                        $"RunPod GraphQL service error ({response.StatusCode}) — validation unavailable");
                }

                if (!IsSuccessStatusCode(response.StatusCode))
                {
                    return ValidationResult.HasHttpError(response.StatusCode,
                        $"Unexpected status {response.StatusCode}. Body: {TruncateResponse(responseBody)}");
                }

                // GraphQL 200 OK — check for body errors
                if (responseBody.Contains("\"errors\"") && !responseBody.Contains("\"myself\""))
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode,
                        "Invalid RunPod API key — GraphQL returned auth error");
                }

                var result = ValidationResult.Success(response.StatusCode, "Valid RunPod key");
                result.RawResponse = responseBody;
                result.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true,
                    ["gpu_availability_checked"] = true
                };

                if (activePodCount.HasValue)
                {
                    result.Metadata["active_pod_count"] = activePodCount.Value;
                }

                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        bool underBalance = false;
                        double clientBalance = 0;
                        double spendPerHr = 0;
                        double minBalance = 0;

                        if (data.TryGetProperty("myself", out var myself))
                        {
                            if (myself.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String)
                            {
                                result.AccountTier = email.GetString();
                                result.Metadata["email"] = email.GetString() ?? "";
                            }

                            if (myself.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                            {
                                result.Metadata["user_id"] = idProp.GetString() ?? "";
                            }

                            if (myself.TryGetProperty("clientBalance", out var clientBalProp) && clientBalProp.ValueKind == JsonValueKind.Number)
                            {
                                clientBalance = clientBalProp.GetDouble();
                                result.Balance = $"${clientBalance:N4}";
                                result.Metadata["clientBalance"] = clientBalance;
                            }

                            if (myself.TryGetProperty("currentSpendPerHr", out var spendProp) && spendProp.ValueKind == JsonValueKind.Number)
                            {
                                spendPerHr = spendProp.GetDouble();
                                result.Metadata["currentSpendPerHr"] = spendPerHr;
                            }

                            if (myself.TryGetProperty("minBalance", out var minBalProp) && minBalProp.ValueKind == JsonValueKind.Number)
                            {
                                minBalance = minBalProp.GetDouble();
                                result.Metadata["minBalance"] = minBalance;
                            }

                            if (myself.TryGetProperty("underBalance", out var underBalProp) &&
                                (underBalProp.ValueKind == JsonValueKind.True || underBalProp.ValueKind == JsonValueKind.False))
                            {
                                underBalance = underBalProp.GetBoolean();
                                result.IsQuotaExceeded = underBalance;
                                result.Metadata["underBalance"] = underBalance;
                            }

                            if (myself.TryGetProperty("creditAlertThreshold", out var alertProp) && alertProp.ValueKind == JsonValueKind.Number)
                            {
                                result.Metadata["creditAlertThreshold"] = alertProp.GetDouble();
                            }
                        }

                        // Parse real-time GPU stock capacity & supported types
                        var supportedGpus = new List<string>();
                        var inStockGpus = new List<string>();
                        var outOfStockGpus = new List<string>();
                        var gpuDetails = new List<Dictionary<string, object>>();

                        if (data.TryGetProperty("gpuTypes", out var gpuTypesArr) && gpuTypesArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var gpu in gpuTypesArr.EnumerateArray())
                            {
                                string name = gpu.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : "";
                                string id = gpu.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                                string gpuName = !string.IsNullOrEmpty(name) ? name : id;

                                if (string.IsNullOrEmpty(gpuName)) continue;

                                bool secure = gpu.TryGetProperty("secureCloud", out var s) && s.GetBoolean();
                                bool community = gpu.TryGetProperty("communityCloud", out var c) && c.GetBoolean();

                                string stockStatus = "Unknown";
                                double uninterruptablePrice = 0;
                                string availableGpuCounts = "0";

                                if (gpu.TryGetProperty("lowestPrice", out var lp) && lp.ValueKind == JsonValueKind.Object)
                                {
                                    if (lp.TryGetProperty("stockStatus", out var ssProp) && ssProp.ValueKind == JsonValueKind.String)
                                    {
                                        stockStatus = ssProp.GetString() ?? "Unknown";
                                    }

                                    if (lp.TryGetProperty("uninterruptablePrice", out var priceProp) && priceProp.ValueKind == JsonValueKind.Number)
                                    {
                                        uninterruptablePrice = priceProp.GetDouble();
                                    }

                                    if (lp.TryGetProperty("availableGpuCounts", out var cntProp))
                                    {
                                        availableGpuCounts = cntProp.ToString();
                                    }
                                }

                                if (!supportedGpus.Contains(gpuName))
                                    supportedGpus.Add(gpuName);

                                bool isInStock = !string.Equals(stockStatus, "None", StringComparison.OrdinalIgnoreCase) &&
                                                 !string.Equals(stockStatus, "Unknown", StringComparison.OrdinalIgnoreCase);

                                if (isInStock)
                                {
                                    if (!inStockGpus.Contains(gpuName)) inStockGpus.Add(gpuName);
                                }
                                else
                                {
                                    if (!outOfStockGpus.Contains(gpuName)) outOfStockGpus.Add(gpuName);
                                }

                                var detailDict = new Dictionary<string, object>
                                {
                                    ["gpu_name"] = gpuName,
                                    ["id"] = id,
                                    ["secureCloud"] = secure,
                                    ["communityCloud"] = community,
                                    ["stockStatus"] = stockStatus,
                                    ["uninterruptablePrice"] = uninterruptablePrice,
                                    ["availableGpuCounts"] = availableGpuCounts
                                };
                                gpuDetails.Add(detailDict);
                            }

                            result.Metadata["supported_gpu_types"] = supportedGpus;
                            result.Metadata["in_stock_gpu_types"] = inStockGpus;
                            result.Metadata["out_of_stock_gpu_types"] = outOfStockGpus;
                            result.Metadata["supported_gpu_count"] = supportedGpus.Count;
                            result.Metadata["in_stock_gpu_count"] = inStockGpus.Count;
                            result.Metadata["gpu_details"] = gpuDetails;
                        }

                        // Account must have balance AND real-time GPU stock capacity to provision a pod
                        bool canCreatePod = !underBalance && clientBalance >= minBalance && inStockGpus.Count > 0;
                        result.Metadata["can_create_pod"] = canCreatePod;

                        string podInfo = activePodCount.HasValue ? $"{activePodCount.Value} active pod(s), " : "";
                        string stockInfo = inStockGpus.Count > 0 ? $"{inStockGpus.Count}/{supportedGpus.Count} GPU type(s) in stock" : "No GPU stock available";
                        result.Detail = $"Valid RunPod key — {podInfo}Balance: ${clientBalance:N4}, Can Provision: {canCreatePod} ({stockInfo})";
                    }
                }
                catch { /* Best effort parsing */ }

                return result;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) &&
                   apiKey.StartsWith("rpa_") &&
                   apiKey.Length >= 44;
        }
    }
}
