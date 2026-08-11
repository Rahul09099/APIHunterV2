using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Provider for Fireworks AI API keys.
    /// Authentication: Bearer {apiKey}
    /// Verification strategy:
    ///   1. GET /v1/accounts (lists accounts, authenticates credential)
    ///   2. GET /v1/accounts/{account_id}/quotas (retrieves account quota and usage info)
    /// Docs: https://docs.fireworks.ai/api-reference/list-accounts
    /// </summary>
    [ApiProvider]
    public class FireworksProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Fireworks AI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.FireworksAI;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bfw_[A-Za-z0-9_-]{30,80}\b",
            @"FIREWORKS_API_KEY\s*[:=]\s*['""]?([A-Za-z0-9_-]{30,})['""]?",
            @"FIREWORKS_KEY\s*[:=]\s*['""]?([A-Za-z0-9_-]{30,})['""]?",
            @"fireworks[._-]?api[._-]?key\s*[:=]\s*['""]?([A-Za-z0-9_-]{30,})['""]?"
        ];

        public FireworksProvider() : base() { }
        public FireworksProvider(ILogger<FireworksProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // 1. Get Accounts — authenticates API key
                using var accountRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.fireworks.ai/v1/accounts");
                accountRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var accountResponse = await httpClient.SendAsync(accountRequest);
                string accountBody = await accountResponse.Content.ReadAsStringAsync();

                _logger?.LogDebug("Fireworks AI accounts response: Status={StatusCode}, Body={Body}",
                    accountResponse.StatusCode, TruncateResponse(accountBody));

                ValidationResult result;

                if (!IsSuccessStatusCode(accountResponse.StatusCode))
                {
                    result = accountResponse.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized =>
                            ValidationResult.IsUnauthorized(accountResponse.StatusCode, "Invalid Fireworks AI API key"),
                        HttpStatusCode.Forbidden =>
                            new ValidationResult
                            {
                                Status = ValidationAttemptStatus.ValidationUnavailable,
                                HttpStatusCode = accountResponse.StatusCode,
                                Detail = "Fireworks AI account access forbidden; key validity could not be determined."
                            },
                        (HttpStatusCode)429 =>
                            new ValidationResult
                            {
                                Status = ValidationAttemptStatus.ValidationUnavailable,
                                HttpStatusCode = accountResponse.StatusCode,
                                Detail = "Fireworks AI rate limit exceeded (HTTP 429)"
                            },
                        _ => ValidationResult.HasHttpError(accountResponse.StatusCode,
                            $"Account check failed with HTTP {accountResponse.StatusCode}. Body: {TruncateResponse(accountBody)}")
                    };
                    result.RawResponse = accountBody;
                    return result;
                }

                result = ValidationResult.Success(accountResponse.StatusCode, "Valid Fireworks AI key");
                result.Metadata = new Dictionary<string, object>
                {
                    ["authentication_valid"] = true
                };

                try
                {
                    using var doc = JsonDocument.Parse(accountBody);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("accounts", out var accounts) &&
                        accounts.ValueKind == JsonValueKind.Array &&
                        accounts.GetArrayLength() > 0)
                    {
                        var firstAccount = accounts[0];
                        string? accountId = null;
                        string? displayName = null;

                        if (firstAccount.TryGetProperty("name", out var nameProp))
                            accountId = nameProp.GetString();

                        if (firstAccount.TryGetProperty("displayName", out var dispProp))
                            displayName = dispProp.GetString();

                        if (firstAccount.TryGetProperty("suspendState", out var suspendProp))
                            result.Metadata["suspend_state"] = suspendProp.GetString() ?? "";

                        result.AccountTier = !string.IsNullOrEmpty(displayName) ? displayName : accountId;
                        result.Detail = $"Valid Fireworks AI key — {accounts.GetArrayLength()} account(s)";

                        if (!string.IsNullOrEmpty(accountId))
                        {
                            result.Metadata["account_id"] = accountId;

                            // 2. Fetch Quotas for account (official Fireworks API: GET /v1/accounts/{account_id}/quotas)
                            using var quotaRequest = new HttpRequestMessage(HttpMethod.Get,
                                $"https://api.fireworks.ai/v1/accounts/{accountId}/quotas");
                            quotaRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                            var quotaResponse = await httpClient.SendAsync(quotaRequest);
                            var quotaBody = await quotaResponse.Content.ReadAsStringAsync();

                            _logger?.LogDebug(
                                "Fireworks AI quota response: Status={StatusCode}, Body={Body}",
                                quotaResponse.StatusCode,
                                TruncateResponse(quotaBody));

                            if (quotaResponse.IsSuccessStatusCode)
                            {
                                try
                                {
                                    using var quotaDoc = JsonDocument.Parse(quotaBody);

                                    if (quotaDoc.RootElement.TryGetProperty("quotas", out var quotas) &&
                                        quotas.ValueKind == JsonValueKind.Array)
                                    {
                                        result.Metadata["quotas_count"] = quotas.GetArrayLength();

                                        foreach (var quota in quotas.EnumerateArray())
                                        {
                                            if (quota.TryGetProperty("name", out var quotaNameProp))
                                            {
                                                var name = quotaNameProp.GetString();
                                                if (!string.IsNullOrEmpty(name))
                                                {
                                                    if (quota.TryGetProperty("value", out var valueProp))
                                                        result.Metadata[$"quota_{name}_value"] = valueProp.GetString() ?? "";

                                                    if (quota.TryGetProperty("maxValue", out var maxProp))
                                                        result.Metadata[$"quota_{name}_max"] = maxProp.GetString() ?? "";

                                                    if (quota.TryGetProperty("usage", out var usageProp) &&
                                                        usageProp.ValueKind == JsonValueKind.Number &&
                                                        usageProp.TryGetDouble(out var usageVal))
                                                    {
                                                        result.Metadata[$"quota_{name}_usage"] = usageVal;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (JsonException ex)
                                {
                                    _logger?.LogDebug(ex, "Failed to parse Fireworks AI quota response");
                                }
                            }
                        }
                    }
                    else
                    {
                        result.Detail = "Valid Fireworks AI key — no accounts found";
                    }
                }
                catch { result.Detail = "Valid Fireworks AI key"; }

                result.RawResponse = accountBody;
                return result;
            }
            catch (Exception ex)
            {
                return ValidationResult.HasNetworkError(ex.Message);
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKey) && apiKey.Length >= 30;
        }
    }
}
