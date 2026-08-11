using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common;

namespace UnsecuredAPIKeys.Providers.Cloud_Providers
{
    [ApiProvider]
    public class AWSIAMProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "AWS IAM";
        public override ApiTypeEnum ApiType => ApiTypeEnum.AWSIAM;

        public override IEnumerable<string> RegexPatterns =>
        [
            // AWS Access Key ID (long-term credentials)
            @"\bAKIA[0-9A-Z]{16}\b",
            
            // AWS Session Token Access Key (temporary credentials)
            @"\bASIA[0-9A-Z]{16}\b",
            
            // Environment variable patterns
            @"AWS_ACCESS_KEY_ID\s*=\s*['""]?((?:AKIA|ASIA)[0-9A-Z]{16})['""]?",
            @"aws_access_key_id\s*=\s*['""]?((?:AKIA|ASIA)[0-9A-Z]{16})['""]?",
            
            // Secret key patterns (base64-like)
            @"AWS_SECRET_ACCESS_KEY\s*=\s*['""]?([A-Za-z0-9/+=]{20,})['""]?",
            @"aws_secret_access_key\s*=\s*['""]?([A-Za-z0-9/+=]{20,})['""]?",

            // Session token patterns
            @"AWS_SESSION_TOKEN\s*=\s*['""]?([A-Za-z0-9/+=]{50,})['""]?",
            @"aws_session_token\s*=\s*['""]?([A-Za-z0-9/+=]{50,})['""]?",

            // Combined patterns in code
            @"AccessKeyId['""]?\s*[:=]\s*['""]?((?:AKIA|ASIA)[0-9A-Z]{16})['""]?",
            @"SecretAccessKey['""]?\s*[:=]\s*['""]?([A-Za-z0-9/+=]{20,})['""]?"
        ];

        public AWSIAMProvider() : base() { }
        public AWSIAMProvider(ILogger<AWSIAMProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Extract credential tuple (AccessKeyId, SecretAccessKey, SessionToken)
                var (accessKeyId, secretAccessKey, sessionToken) = ExtractCredentialTuple(apiKey);
                
                if (string.IsNullOrEmpty(secretAccessKey))
                {
                    return ValidationResult.HasProviderSpecificError(
                        "Secret Access Key not found in context");
                }
                
                // Verify credentials with STS
                var stsResponse = await VerifyCredentialsAsync(accessKeyId, secretAccessKey, sessionToken);
                
                // Extract metadata
                var metadata = ExtractMetadataFromStsResponse(stsResponse, accessKeyId);
                
                // Enumerate permissions via local IAM client (only for IAM user principals)
                List<string> policies = new();
                if (metadata.IsIamUser)
                {
                    try
                    {
                        using var iamClient = CreateIamClient(accessKeyId, secretAccessKey, sessionToken);
                        policies = await EnumeratePermissionsAsync(iamClient, metadata);
                    }
                    catch (AmazonIdentityManagementServiceException ex) when (ex.ErrorCode == "AccessDenied")
                    {
                        _logger?.LogDebug("Permission enumeration denied for {User}", metadata.UserName);
                        policies = new List<string> { "Permission enumeration denied (AccessDenied)" };
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "IAM policy enumeration unavailable for {User}", metadata.UserName);
                    }
                }
                else if (metadata.IsAssumedRole)
                {
                    policies = new List<string> { "Assumed Role Principal (User policy enumeration not applicable)" };
                }
                
                // Calculate risk level heuristic
                var riskLevel = CalculateRiskLevelHeuristic(policies, metadata.IsRootAccount);
                
                // Build validation result with AWS metadata
                var result = ValidationResult.Success(
                    HttpStatusCode.OK,
                    $"Valid AWS IAM credential - {metadata.CredentialType}");
                
                result.AwsAccountId = metadata.AccountId;
                result.AwsUserArn = metadata.UserArn;
                result.AwsUserId = metadata.UserId;
                result.AwsCredentialType = metadata.CredentialType;
                result.AwsAttachedPolicies = policies;
                result.AwsRiskLevel = riskLevel;
                result.AwsIsRootAccount = metadata.IsRootAccount;
                
                // Serialize raw STS response body
                try
                {
                    result.RawResponse = JsonSerializer.Serialize(new
                    {
                        stsResponse.Account,
                        stsResponse.Arn,
                        stsResponse.UserId,
                        stsResponse.ResponseMetadata
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to serialize AWS STS response");
                }

                return result;
            }
            catch (AmazonSecurityTokenServiceException ex)
            {
                if (ex.ErrorCode is "InvalidClientTokenId" or "UnrecognizedClientException" or "SignatureDoesNotMatch" or "InvalidAccessKeyId")
                {
                    return ValidationResult.IsUnauthorized(HttpStatusCode.Unauthorized, $"Invalid AWS credentials ({ex.ErrorCode})");
                }

                if (ex.StatusCode == HttpStatusCode.Forbidden || ex.ErrorCode == "AccessDenied")
                {
                    return ValidationResult.HasHttpError(HttpStatusCode.Forbidden, $"AWS API access denied: {ex.Message}");
                }

                if ((int)ex.StatusCode == 429)
                {
                    return new ValidationResult
                    {
                        Status = ValidationAttemptStatus.ValidationUnavailable,
                        IsQuotaExceeded = true,
                        HttpStatusCode = ex.StatusCode,
                        Detail = "AWS API rate limit exhausted (HTTP 429)"
                    };
                }

                return ValidationResult.HasHttpError(ex.StatusCode, $"STS Error [{ex.ErrorCode}]: {ex.Message}");
            }
            catch (AmazonServiceException ex) when ((int)ex.StatusCode == 429)
            {
                return new ValidationResult
                {
                    Status = ValidationAttemptStatus.ValidationUnavailable,
                    IsQuotaExceeded = true,
                    HttpStatusCode = ex.StatusCode,
                    Detail = "AWS API rate limit exhausted (HTTP 429)"
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "AWS validation error");
                return ValidationResult.HasNetworkError($"AWS API error: {ex.Message}");
            }
        }

        protected override bool IsValidKeyFormat(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return false;

            // Case 1: Delimited format (AKIA:::secret or AKIA:::secret:::sessionToken or AKIA|secret|sessionToken)
            if (apiKey.Contains(":::") || apiKey.Contains("|"))
            {
                var parts = apiKey.Split(new[] { ":::", "|" }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts.Length <= 3)
                {
                    var accessKeyId = parts[0].Trim();
                    var secretKey = parts[1].Trim();
                    
                    if (!IsValidAccessKeyIdFormat(accessKeyId))
                        return false;
                    
                    if (string.IsNullOrWhiteSpace(secretKey))
                        return false;
                    
                    return true;
                }
                return false;
            }

            // Case 2: Standalone Access Key ID
            return IsValidAccessKeyIdFormat(apiKey);
        }

        private bool IsValidAccessKeyIdFormat(string accessKeyId)
        {
            if (string.IsNullOrWhiteSpace(accessKeyId))
                return false;

            return (accessKeyId.StartsWith("AKIA") || accessKeyId.StartsWith("ASIA")) && 
                   accessKeyId.Length == 20;
        }

        private (string accessKeyId, string secretAccessKey, string? sessionToken) ExtractCredentialTuple(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("API key cannot be null or empty", nameof(apiKey));
            }

            if (apiKey.Contains(":::") || apiKey.Contains("|"))
            {
                var parts = apiKey.Split(new[] { ":::", "|" }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var accessKeyId = parts[0].Trim();
                    var secretKey = parts[1].Trim();
                    string? sessionToken = parts.Length >= 3 ? parts[2].Trim() : null;
                    
                    if (!IsValidAccessKeyIdFormat(accessKeyId))
                    {
                        throw new ArgumentException($"Invalid Access Key ID format: {accessKeyId}", nameof(apiKey));
                    }
                    
                    if (string.IsNullOrWhiteSpace(secretKey))
                    {
                        throw new ArgumentException("Secret Access Key cannot be empty in delimited format", nameof(apiKey));
                    }
                    
                    if (sessionToken != null && (sessionToken.Length < 16 || sessionToken.Length > 2048))
                    {
                        throw new ArgumentException($"Invalid AWS Session Token length ({sessionToken.Length})", nameof(apiKey));
                    }
                    
                    return (accessKeyId, secretKey, sessionToken);
                }
                
                throw new ArgumentException("Delimited format must contain at least Access Key ID and Secret Access Key", nameof(apiKey));
            }
            
            if (apiKey.StartsWith("AKIA") || apiKey.StartsWith("ASIA"))
            {
                if (!IsValidAccessKeyIdFormat(apiKey))
                {
                    throw new ArgumentException($"Invalid Access Key ID format: {apiKey}", nameof(apiKey));
                }
                
                return (apiKey, string.Empty, null);
            }
            
            throw new ArgumentException($"Invalid AWS credential format: {apiKey}", nameof(apiKey));
        }

        private IAmazonSecurityTokenService CreateStsClient(
            string accessKeyId,
            string secretAccessKey,
            string? sessionToken = null,
            string region = "us-east-1")
        {
            AWSCredentials credentials = string.IsNullOrWhiteSpace(sessionToken)
                ? new BasicAWSCredentials(accessKeyId, secretAccessKey)
                : new SessionAWSCredentials(accessKeyId, secretAccessKey, sessionToken);

            var config = new AmazonSecurityTokenServiceConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(region),
                Timeout = TimeSpan.FromSeconds(10),
                MaxErrorRetry = 2
            };

            return new AmazonSecurityTokenServiceClient(credentials, config);
        }

        private IAmazonIdentityManagementService CreateIamClient(
            string accessKeyId,
            string secretAccessKey,
            string? sessionToken = null)
        {
            AWSCredentials credentials = string.IsNullOrWhiteSpace(sessionToken)
                ? new BasicAWSCredentials(accessKeyId, secretAccessKey)
                : new SessionAWSCredentials(accessKeyId, secretAccessKey, sessionToken);

            var config = new AmazonIdentityManagementServiceConfig
            {
                RegionEndpoint = RegionEndpoint.USEast1,
                Timeout = TimeSpan.FromSeconds(10),
                MaxErrorRetry = 2
            };

            return new AmazonIdentityManagementServiceClient(credentials, config);
        }

        private async Task<GetCallerIdentityResponse> VerifyCredentialsAsync(
            string accessKeyId,
            string secretAccessKey,
            string? sessionToken = null)
        {
            using var stsClient = CreateStsClient(accessKeyId, secretAccessKey, sessionToken, "us-east-1");
            var request = new GetCallerIdentityRequest();
            return await stsClient.GetCallerIdentityAsync(request);
        }

        private async Task<List<string>> EnumeratePermissionsAsync(
            IAmazonIdentityManagementService iamClient,
            AwsMetadata metadata)
        {
            if (!metadata.IsIamUser || string.IsNullOrEmpty(metadata.UserName) || metadata.UserName == "unknown" || metadata.IsRootAccount)
            {
                return new List<string>();
            }

            var policies = new List<string>();

            var request = new ListAttachedUserPoliciesRequest
            {
                UserName = metadata.UserName
            };

            var response = await iamClient.ListAttachedUserPoliciesAsync(request);
            foreach (var policy in response.AttachedPolicies)
            {
                policies.Add(policy.PolicyName);
            }

            return policies;
        }

        private AwsMetadata ExtractMetadataFromStsResponse(
            GetCallerIdentityResponse stsResponse,
            string accessKeyId)
        {
            return new AwsMetadata
            {
                AccountId = stsResponse.Account ?? "Unknown",
                UserArn = stsResponse.Arn ?? "Unknown",
                UserId = stsResponse.UserId ?? "Unknown",
                CredentialType = accessKeyId.StartsWith("ASIA") ? "Temporary (Session Token)" : "Long-term (IAM User)"
            };
        }

        private string CalculateRiskLevelHeuristic(List<string> policies, bool isRootAccount)
        {
            if (isRootAccount) return "Critical";

            foreach (var policy in policies)
            {
                var lower = policy.ToLowerInvariant();
                if (lower.Contains("administratoraccess")) return "Critical";
                if (lower.Contains("poweruseraccess") || lower.Contains("fullaccess")) return "High";
            }

            if (policies.Count > 0) return "Medium";

            return "Low";
        }

        private class AwsMetadata
        {
            public string AccountId { get; set; } = string.Empty;
            public string UserArn { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public string CredentialType { get; set; } = string.Empty;

            public bool IsRootAccount => UserArn.EndsWith(":root", StringComparison.OrdinalIgnoreCase);
            public bool IsIamUser => UserArn.Contains(":user/", StringComparison.OrdinalIgnoreCase);
            public bool IsAssumedRole => UserArn.Contains(":assumed-role/", StringComparison.OrdinalIgnoreCase);

            public string UserName
            {
                get
                {
                    if (string.IsNullOrEmpty(UserArn)) return "unknown";
                    
                    if (IsRootAccount) return "root";

                    if (IsIamUser)
                    {
                        var idx = UserArn.IndexOf(":user/", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0) return UserArn[(idx + 6)..];
                    }

                    if (IsAssumedRole)
                    {
                        var idx = UserArn.IndexOf(":assumed-role/", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0) return UserArn[(idx + 14)..];
                    }

                    var parts = UserArn.Split('/');
                    if (parts.Length > 1) return parts[parts.Length - 1];

                    return "unknown";
                }
            }
        }
    }
}
