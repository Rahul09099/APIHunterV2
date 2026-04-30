using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    [ApiProvider]
    public class KlingAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "KlingAI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.KlingAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"(?:KLING_ACCESS_KEY|kling_access_key|KLING_AK).*?['""]([a-zA-Z0-9]{16,})['""]",
            @"\b[a-zA-Z0-9]{24,}\b" // Fallback for raw keys
        ];

        public KlingAIProvider() : base() { }

        public KlingAIProvider(ILogger<KlingAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            string ak = apiKey;
            string? sk = null;

            // Check if we have a paired key (AK:SK)
            if (apiKey.Contains(':'))
            {
                var parts = apiKey.Split(':');
                ak = parts[0];
                sk = parts[1];
            }

            // If we only have AK, we try a direct Bearer test (some proxy providers support this)
            // But for native Kling, we need the Secret Key to sign a JWT.
            string authHeaderValue = apiKey; 

            if (!string.IsNullOrEmpty(sk))
            {
                try 
                {
                    authHeaderValue = GenerateKlingJwt(ak, sk);
                }
                catch (Exception ex)
                {
                    return ValidationResult.HasHttpError(HttpStatusCode.BadRequest, $"JWT Generation failed: {ex.Message}");
                }
            }
            
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var startMs = nowMs - (7 * 24 * 60 * 60 * 1000); // Look back 7 days for balance/costs
            
            string url = $"https://api-singapore.klingai.com/account/costs?start_time={startMs}&end_time={nowMs}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authHeaderValue);

            try 
            {
                var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                 _logger?.LogDebug("KlingAI API response: Status={StatusCode}, Body={Body}",
                    response.StatusCode, TruncateResponse(responseBody));

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid KlingAI key");

                    try 
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                        if (doc.RootElement.TryGetProperty("data", out var data))
                        {
                            // The new API returns a list of resource packages under 'resource_pack_subscribe_infos'
                            if (data.TryGetProperty("resource_pack_subscribe_infos", out var packs) && 
                                packs.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                double totalRemaining = 0;
                                foreach (var pack in packs.EnumerateArray())
                                {
                                    if (pack.TryGetProperty("remaining_quantity", out var rem))
                                    {
                                        if (rem.ValueKind == System.Text.Json.JsonValueKind.Number)
                                            totalRemaining += rem.GetDouble();
                                        else if (rem.ValueKind == System.Text.Json.JsonValueKind.String && double.TryParse(rem.GetString(), out double val))
                                            totalRemaining += val;
                                    }
                                }
                                result.Balance = $"{totalRemaining} Credits";
                            }
                        }
                    }
                    catch { /* Best effort */ }

                    return result;
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ValidationResult.IsUnauthorized(response.StatusCode);
                }
                 else if ((int)response.StatusCode == 429)
                {
                    return ValidationResult.Success(response.StatusCode, "Rate limited (key is valid)");
                }
                else
                {
                     return ValidationResult.HasHttpError(response.StatusCode, 
                        $"API request failed with status {response.StatusCode}. Response: {TruncateResponse(responseBody)}");
                }
            }
            catch (Exception ex)
            {
                return ValidationResult.HasHttpError(HttpStatusCode.ServiceUnavailable, $"Connection failed: {ex.Message}");
            }
        }

        private string GenerateKlingJwt(string ak, string sk)
        {
            // Standard HS256 JWT Generation for Kling AI
            var header = new { alg = "HS256", typ = "JWT" };
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = new 
            { 
                iss = ak, 
                exp = now + 1800, // 30 mins
                nbf = now - 5 
            };

            string encodedHeader = Base64UrlEncode(System.Text.Json.JsonSerializer.Serialize(header));
            string encodedPayload = Base64UrlEncode(System.Text.Json.JsonSerializer.Serialize(payload));
            
            string dataToSign = $"{encodedHeader}.{encodedPayload}";
            byte[] keyBytes = System.Text.Encoding.UTF8.GetBytes(sk);
            byte[] dataBytes = System.Text.Encoding.UTF8.GetBytes(dataToSign);
            
            using var hmac = new System.Security.Cryptography.HMACSHA256(keyBytes);
            byte[] hashBytes = hmac.ComputeHash(dataBytes);
            string encodedSignature = Base64UrlEncode(hashBytes);
            
            return $"{dataToSign}.{encodedSignature}";
        }

        private static string Base64UrlEncode(string input) => Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(input));

        private static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input)
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            // Accept AK:SK pairs or single keys of length > 16
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length > 16;
        }
    }
}
