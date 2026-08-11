using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    /// <summary>
    /// Provider for Kling AI API keys.
    /// Authentication requires an Access Key (AK) and Secret Key (SK) pair formatted as "AK:SK".
    /// Auth strategy: HS256 signed JWT in the Authorization Bearer header.
    /// Docs: https://api.klingai.com
    /// </summary>
    [ApiProvider]
    public class KlingAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "KlingAI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.KlingAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"(?i)\bKLING_ACCESS_KEY\s*[:=]\s*['""]?([A-Za-z0-9]{16,})['""]?",
            @"(?i)\bKLING_SECRET_KEY\s*[:=]\s*['""]?([A-Za-z0-9]{16,})['""]?",
            @"(?i)\bKLING_AK\s*[:=]\s*['""]?([A-Za-z0-9]{16,})['""]?",
            @"(?i)\bKLING_SK\s*[:=]\s*['""]?([A-Za-z0-9]{16,})['""]?"
        ];

        public KlingAIProvider() : base() { }
        public KlingAIProvider(ILogger<KlingAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            var parts = apiKey.Split(':', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                return ValidationResult.HasHttpError(HttpStatusCode.BadRequest,
                    "KlingAI authentication requires a paired Access Key and Secret Key formatted as 'AK:SK'.");
            }

            string ak = parts[0].Trim();
            string sk = parts[1].Trim();

            string authHeaderValue;
            try
            {
                authHeaderValue = GenerateKlingJwt(ak, sk);
            }
            catch (Exception ex)
            {
                return ValidationResult.HasHttpError(HttpStatusCode.BadRequest, $"JWT Generation failed: {ex.Message}");
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var startMs = nowMs - (7L * 24 * 60 * 60 * 1000);

            // Primary endpoint: api.klingai.com, fallback to regional api-singapore.klingai.com
            string url = $"https://api.klingai.com/v1/account/costs?start_time={startMs}&end_time={nowMs}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authHeaderValue);

            try
            {
                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                // If primary 404s, try regional endpoint
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    string regionalUrl = $"https://api-singapore.klingai.com/account/costs?start_time={startMs}&end_time={nowMs}";
                    using var regRequest = new HttpRequestMessage(HttpMethod.Get, regionalUrl);
                    regRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authHeaderValue);
                    response = await httpClient.SendAsync(regRequest);
                    responseBody = await response.Content.ReadAsStringAsync();
                }

                _logger?.LogDebug("KlingAI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                ValidationResult result;

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    result = ValidationResult.Success(response.StatusCode, "Valid KlingAI credentials");
                    result.Metadata = new Dictionary<string, object>
                    {
                        ["authentication_valid"] = true,
                        ["access_key"] = ak
                    };

                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("data", out var data))
                        {
                            if (data.TryGetProperty("resource_pack_subscribe_infos", out var packs) &&
                                packs.ValueKind == JsonValueKind.Array)
                            {
                                int packCount = packs.GetArrayLength();
                                result.Metadata["resource_package_count"] = packCount;

                                var packList = new List<Dictionary<string, object>>();
                                foreach (var pack in packs.EnumerateArray())
                                {
                                    var item = new Dictionary<string, object>();
                                    if (pack.TryGetProperty("resource_pack_name", out var pName))
                                        item["name"] = pName.GetString() ?? "";

                                    if (pack.TryGetProperty("remaining_quantity", out var rem) && rem.ValueKind == JsonValueKind.Number)
                                        item["remaining_quantity"] = rem.GetDouble();

                                    if (pack.TryGetProperty("total_quantity", out var tot) && tot.ValueKind == JsonValueKind.Number)
                                        item["total_quantity"] = tot.GetDouble();

                                    if (pack.TryGetProperty("status", out var pStatus))
                                        item["status"] = pStatus.GetString() ?? "";

                                    if (item.Count > 0)
                                        packList.Add(item);
                                }
                                if (packList.Count > 0)
                                    result.Metadata["resource_packages"] = packList;
                            }
                        }
                    }
                    catch
                    {
                        // Json parse failure is best-effort metadata
                    }

                    result.Balance = "N/A (check KlingAI account dashboard)";
                    result.RawResponse = responseBody;
                    return result;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    result = ValidationResult.IsUnauthorized(response.StatusCode, "Invalid KlingAI Access Key or Secret Key");
                    result.RawResponse = responseBody;
                    return result;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    result = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = "KlingAI request forbidden; credential validity could not be determined."
                    };
                    result.RawResponse = responseBody;
                    return result;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    result = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = "KlingAI rate limit exceeded; credential validity could not be determined."
                    };
                    result.RawResponse = responseBody;
                    return result;
                }

                if ((int)response.StatusCode >= 500)
                {
                    result = new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = $"KlingAI service unavailable (HTTP {(int)response.StatusCode})"
                    };
                    result.RawResponse = responseBody;
                    return result;
                }

                result = ValidationResult.HasHttpError(response.StatusCode,
                    $"API request failed with status {response.StatusCode}. Response: {TruncateResponse(responseBody)}");
                result.RawResponse = responseBody;
                return result;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        private string GenerateKlingJwt(string ak, string sk)
        {
            var header = new { alg = "HS256", typ = "JWT" };
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = new
            {
                iss = ak,
                exp = now + 1800,
                nbf = now - 5
            };

            string encodedHeader = Base64UrlEncode(JsonSerializer.Serialize(header));
            string encodedPayload = Base64UrlEncode(JsonSerializer.Serialize(payload));

            string dataToSign = $"{encodedHeader}.{encodedPayload}";
            byte[] keyBytes = Encoding.UTF8.GetBytes(sk);
            byte[] dataBytes = Encoding.UTF8.GetBytes(dataToSign);

            using var hmac = new HMACSHA256(keyBytes);
            byte[] hashBytes = hmac.ComputeHash(dataBytes);
            string encodedSignature = Base64UrlEncode(hashBytes);

            return $"{dataToSign}.{encodedSignature}";
        }

        private static string Base64UrlEncode(string input) => Base64UrlEncode(Encoding.UTF8.GetBytes(input));

        private static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input)
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return false;

            var parts = apiKey.Split(':', 2);
            if (parts.Length != 2) return false;

            var ak = parts[0].Trim();
            var sk = parts[1].Trim();

            return ak.Length >= 16 && sk.Length >= 16;
        }
    }
}
