using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.Communication_Providers
{
    /// <summary>
    /// Provider for Stripe API Secret, Restricted, Organization & Webhook Keys
    /// </summary>
    [ApiProvider]
    public class StripeProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "Stripe";
        public override ApiTypeEnum ApiType => ApiTypeEnum.Stripe;

        public override IEnumerable<string> RegexPatterns =>
        [
            @"\bsk_(?:live|test|org)_[0-9a-zA-Z]{20,}\b",
            @"\brk_(?:live|test)_[0-9a-zA-Z]{20,}\b",
            @"\bwhsec_[0-9a-zA-Z]{20,}\b",
            @"\bpk_(?:live|test)_[0-9a-zA-Z]{20,}\b",
            @"STRIPE_SECRET\s*=\s*['""]?([A-Za-z0-9_-]+)['""]?",
            @"STRIPE_WEBHOOK_SECRET\s*=\s*['""]?([A-Za-z0-9_-]+)['""]?"
        ];

        public StripeProvider() : base() { }
        public StripeProvider(ILogger<StripeProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Webhook secret candidates cannot be validated via /v1/balance API
                if (apiKey.StartsWith("whsec_", StringComparison.OrdinalIgnoreCase))
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Candidate,
                        AccountTier = "Webhook Signing Secret Candidate",
                        Detail = "Stripe Webhook Secret candidate detected; signing secret requires signed payload verification."
                    };
                }

                // Publishable keys are client-side identifiers, not secrets
                if (apiKey.StartsWith("pk_", StringComparison.OrdinalIgnoreCase))
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Candidate,
                        AccountTier = "Publishable Key Candidate",
                        Detail = "Stripe Publishable Key candidate detected; client-side application identifier."
                    };
                }

                // Organization secret keys require Stripe-Context and Stripe-Version headers
                if (apiKey.StartsWith("sk_org_", StringComparison.OrdinalIgnoreCase))
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.Candidate,
                        AccountTier = InferKeyType(apiKey),
                        Detail = "Stripe Organization Key candidate detected; live validation requires explicit Stripe-Context header."
                    };
                }

                // Live, Test, and Restricted Secret Key verification via Balance endpoint
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.stripe.com/v1/balance");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                _logger?.LogDebug("Stripe API response status: {StatusCode}", response.StatusCode);

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    var result = ValidationResult.Success(response.StatusCode, "Valid Stripe API Key");
                    result.AccountTier = InferKeyType(apiKey);

                    try
                    {
                        using var doc = JsonDocument.Parse(responseBody);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("available", out var availableArr) && availableArr.ValueKind == JsonValueKind.Array)
                        {
                            var balanceStrings = new List<string>();
                            foreach (var item in availableArr.EnumerateArray())
                            {
                                if (item.TryGetProperty("amount", out var am) && item.TryGetProperty("currency", out var cur))
                                {
                                    double amount = am.GetInt64() / 100.0;
                                    string currency = cur.GetString()?.ToUpperInvariant() ?? "USD";
                                    balanceStrings.Add($"{amount:F2} {currency}");
                                }
                            }

                            if (balanceStrings.Count > 0)
                            {
                                result.Balance = string.Join(", ", balanceStrings);
                            }
                        }

                        result.Detail = $"Valid Stripe API key ({result.AccountTier}) with balance access.";
                    }
                    catch
                    {
                        result.Detail = $"Valid Stripe API key ({result.AccountTier}).";
                    }

                    return result;
                }

                var stripeError = ExtractStripeError(responseBody);

                // HTTP 401 Unauthorized -> Confirmed Invalid Key
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    string detailMessage = !string.IsNullOrWhiteSpace(stripeError.Message)
                        ? $"Stripe rejected API key: {stripeError.Message}"
                        : "Stripe rejected API key as invalid or revoked.";

                    var unauthResult = ValidationResult.IsUnauthorized(response.StatusCode, detailMessage);
                    if (!string.IsNullOrWhiteSpace(stripeError.Type) || !string.IsNullOrWhiteSpace(stripeError.Code))
                    {
                        unauthResult.Metadata ??= new Dictionary<string, object>();
                        if (!string.IsNullOrWhiteSpace(stripeError.Type))
                            unauthResult.Metadata["StripeErrorType"] = stripeError.Type;
                        if (!string.IsNullOrWhiteSpace(stripeError.Code))
                            unauthResult.Metadata["StripeErrorCode"] = stripeError.Code;
                    }

                    return unauthResult;
                }

                // HTTP 403 Forbidden: For Restricted Keys (rk_), 403 means endpoint permission restriction
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    if (apiKey.StartsWith("rk_", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ValidationResult
                        {
                            Status = ValidationAttemptStatus.ValidationUnavailable,
                            HttpStatusCode = response.StatusCode,
                            AccountTier = InferKeyType(apiKey),
                            Detail = "Stripe rejected /v1/balance access for this restricted key; key validity could not be established from this endpoint."
                        };
                    }

                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        AccountTier = InferKeyType(apiKey),
                        Detail = "Stripe forbidden response; request restricted by organization or security policy."
                    };
                }

                // Transient rate limits, timeouts, or Stripe 5xx server issues
                if (response.StatusCode == HttpStatusCode.RequestTimeout ||
                    response.StatusCode == HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500)
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        HttpStatusCode = response.StatusCode,
                        Detail = "Stripe API endpoint is temporarily unavailable."
                    };
                }

                return ValidationResult.HasHttpError(response.StatusCode, "Stripe validation could not be completed.");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to validate Stripe key");
                return ValidationResult.HasProviderSpecificError("Stripe validation failed.");
            }
        }

        private static string InferKeyType(string apiKey)
        {
            if (apiKey.StartsWith("sk_live_", StringComparison.OrdinalIgnoreCase)) return "Live Secret Key";
            if (apiKey.StartsWith("sk_test_", StringComparison.OrdinalIgnoreCase)) return "Test Secret Key";
            if (apiKey.StartsWith("sk_org_", StringComparison.OrdinalIgnoreCase)) return "Organization Secret Key";
            if (apiKey.StartsWith("rk_live_", StringComparison.OrdinalIgnoreCase)) return "Restricted Live Key";
            if (apiKey.StartsWith("rk_test_", StringComparison.OrdinalIgnoreCase)) return "Restricted Test Key";
            return "Stripe Secret Key";
        }

        private static (string Type, string Code, string Message) ExtractStripeError(string responseBody)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(responseBody))
                    return (string.Empty, string.Empty, string.Empty);

                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errObj) && errObj.ValueKind == JsonValueKind.Object)
                {
                    string type = errObj.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? string.Empty : string.Empty;
                    string code = errObj.TryGetProperty("code", out var cProp) ? cProp.GetString() ?? string.Empty : string.Empty;
                    string message = errObj.TryGetProperty("message", out var mProp) ? mProp.GetString() ?? string.Empty : string.Empty;

                    return (type, code, message);
                }

                return (string.Empty, string.Empty, string.Empty);
            }
            catch (JsonException)
            {
                return (string.Empty, string.Empty, string.Empty);
            }
        }
    }
}
