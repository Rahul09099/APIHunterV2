using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
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

        // AWS SDK clients
        private IAmazonIdentityManagementService? _iamClient;

        public override IEnumerable<string> RegexPatterns =>
        [
            // AWS Access Key ID (long-term credentials)
            @"\bAKIA[0-9A-Z]{16}\b",
            
            // AWS Session Token (temporary credentials)
            @"\bASIA[0-9A-Z]{16}\b",
            
            // Environment variable patterns
            @"AWS_ACCESS_KEY_ID\s*=\s*['""]?(AKIA[0-9A-Z]{16})['""]?",
            @"aws_access_key_id\s*=\s*['""]?(AKIA[0-9A-Z]{16})['""]?",
            
            // Secret key patterns (40 characters, base64-like)
            @"AWS_SECRET_ACCESS_KEY\s*=\s*['""]?([A-Za-z0-9/+=]{40})['""]?",
            @"aws_secret_access_key\s*=\s*['""]?([A-Za-z0-9/+=]{40})['""]?",
            
            // Combined patterns in code
            @"AccessKeyId['""]?\s*[:=]\s*['""]?(AKIA[0-9A-Z]{16})['""]?",
            @"SecretAccessKey['""]?\s*[:=]\s*['""]?([A-Za-z0-9/+=]{40})['""]?"
        ];

        public AWSIAMProvider() : base() { }

        public AWSIAMProvider(ILogger<AWSIAMProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(string apiKey, HttpClient httpClient)
        {
            try
            {
                // Extract credential pair
                var (accessKeyId, secretAccessKey) = ExtractCredentialPair(apiKey);
                
                if (string.IsNullOrEmpty(secretAccessKey))
                {
                    return ValidationResult.HasProviderSpecificError(
                        "Secret Access Key not found in context");
                }
                
                // Verify credentials with STS
                var stsResponse = await VerifyCredentialsAsync(accessKeyId, secretAccessKey);
                
                // Extract metadata
                var metadata = await ExtractMetadataAsync(stsResponse);
                
                // Enumerate permissions (best effort)
                List<string> policies = new();
                try
                {
                    // Initialize IAM client for permission enumeration
                    _iamClient = CreateIamClient(accessKeyId, secretAccessKey);
                    policies = await EnumeratePermissionsAsync(metadata.UserName);
                }
                catch (AmazonIdentityManagementServiceException ex) 
                    when (ex.ErrorCode == "AccessDenied")
                {
                    _logger?.LogDebug("Permission enumeration denied for {User}", metadata.UserName);
                    metadata.AttachedPolicies = new List<string> { "Permission enumeration denied" };
                }
                
                // Calculate risk level
                var riskLevel = CalculateRiskLevel(policies, metadata.IsRootAccount);
                
                // Build validation result with AWS metadata
                var result = ValidationResult.Success(
                    HttpStatusCode.OK,
                    $"Valid AWS IAM credential - {metadata.CredentialType}");
                
                // Store metadata in ValidationResult for database persistence
                result.AwsAccountId = metadata.AccountId;
                result.AwsUserArn = metadata.UserArn;
                result.AwsUserId = metadata.UserId;
                result.AwsCredentialType = metadata.CredentialType;
                result.AwsAttachedPolicies = policies;
                result.AwsRiskLevel = riskLevel;
                result.AwsIsRootAccount = metadata.IsRootAccount;
                
                return result;
            }
            catch (AmazonSecurityTokenServiceException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                return ValidationResult.IsUnauthorized(HttpStatusCode.Forbidden);
            }
            catch (AmazonServiceException ex) when ((int)ex.StatusCode == 429)
            {
                return ValidationResult.Success(
                    (HttpStatusCode)429,
                    "Rate limited (key is valid)");
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

            // Case 1: Delimited format (AKIA:::secret or AKIA|secret)
            if (apiKey.Contains(":::") || apiKey.Contains("|"))
            {
                var parts = apiKey.Split(new[] { ":::", "|" }, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    var accessKeyId = parts[0].Trim();
                    var secretKey = parts[1].Trim();
                    
                    // Validate Access Key ID format
                    if (!IsValidAccessKeyIdFormat(accessKeyId))
                        return false;
                    
                    // Validate Secret Access Key format (40 characters, base64-like)
                    if (secretKey.Length != 40)
                        return false;
                    
                    return true;
                }
                return false;
            }

            // Case 2: Standalone Access Key ID
            return IsValidAccessKeyIdFormat(apiKey);
        }

        /// <summary>
        /// Validates if a string matches the AWS Access Key ID format.
        /// Valid format: starts with AKIA or ASIA and is exactly 20 characters.
        /// </summary>
        private bool IsValidAccessKeyIdFormat(string accessKeyId)
        {
            if (string.IsNullOrWhiteSpace(accessKeyId))
                return false;

            // Valid AWS Access Key ID starts with AKIA or ASIA and is 20 characters total
            return (accessKeyId.StartsWith("AKIA") || accessKeyId.StartsWith("ASIA")) && 
                   accessKeyId.Length == 20;
        }

        /// <summary>
        /// Extracts the Access Key ID and Secret Access Key from the API key string.
        /// Supports two formats:
        /// 1. Delimited format: "AKIA...:::secret..." or "AKIA...|secret..."
        /// 2. Standalone Access Key ID: "AKIA..." (secret must be found in context by ScraperService)
        /// </summary>
        /// <param name="apiKey">The API key string to parse</param>
        /// <returns>Tuple containing (accessKeyId, secretAccessKey). Secret may be empty if not found in apiKey.</returns>
        /// <exception cref="ArgumentException">Thrown when the API key format is invalid</exception>
        private (string accessKeyId, string secretAccessKey) ExtractCredentialPair(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("API key cannot be null or empty", nameof(apiKey));
            }

            // Case 1: apiKey contains both parts separated by a delimiter
            // Format: "AKIA...:::secret..." or "AKIA...|secret..."
            if (apiKey.Contains(":::") || apiKey.Contains("|"))
            {
                var parts = apiKey.Split(new[] { ":::", "|" }, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    var accessKeyId = parts[0].Trim();
                    var secretKey = parts[1].Trim();
                    
                    // Validate Access Key ID format
                    if (!IsValidAccessKeyIdFormat(accessKeyId))
                    {
                        throw new ArgumentException($"Invalid Access Key ID format: {accessKeyId}", nameof(apiKey));
                    }
                    
                    // Validate Secret Access Key format (40 characters, base64-like)
                    if (secretKey.Length != 40)
                    {
                        throw new ArgumentException($"Invalid Secret Access Key length: expected 40, got {secretKey.Length}", nameof(apiKey));
                    }
                    
                    return (accessKeyId, secretKey);
                }
                
                throw new ArgumentException("Delimited format must contain exactly two parts", nameof(apiKey));
            }
            
            // Case 2: apiKey is just the Access Key ID
            // The ScraperService will need to search the surrounding code context
            // for the Secret Access Key within 50 lines
            if (apiKey.StartsWith("AKIA") || apiKey.StartsWith("ASIA"))
            {
                if (!IsValidAccessKeyIdFormat(apiKey))
                {
                    throw new ArgumentException($"Invalid Access Key ID format: {apiKey}", nameof(apiKey));
                }
                
                // Return empty secret for now - will be handled by ScraperService context search
                return (apiKey, string.Empty);
            }
            
            throw new ArgumentException($"Invalid AWS credential format: {apiKey}", nameof(apiKey));
        }

        /// <summary>
        /// Creates an AWS STS client configured with the provided credentials and region.
        /// </summary>
        /// <param name="accessKeyId">AWS Access Key ID</param>
        /// <param name="secretAccessKey">AWS Secret Access Key</param>
        /// <param name="region">AWS region (default: us-east-1)</param>
        /// <returns>Configured IAmazonSecurityTokenService client</returns>
        private IAmazonSecurityTokenService CreateStsClient(
            string accessKeyId,
            string secretAccessKey,
            string region = "us-east-1")
        {
            var credentials = new BasicAWSCredentials(accessKeyId, secretAccessKey);
            var config = new AmazonSecurityTokenServiceConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(region),
                Timeout = TimeSpan.FromSeconds(10),
                MaxErrorRetry = 2
            };

            return new AmazonSecurityTokenServiceClient(credentials, config);
        }

        /// <summary>
        /// Creates an AWS IAM client configured with the provided credentials.
        /// IAM is a global service but requires a region endpoint (us-east-1 is used by convention).
        /// </summary>
        /// <param name="accessKeyId">AWS Access Key ID</param>
        /// <param name="secretAccessKey">AWS Secret Access Key</param>
        /// <returns>Configured IAmazonIdentityManagementService client</returns>
        private IAmazonIdentityManagementService CreateIamClient(
            string accessKeyId,
            string secretAccessKey)
        {
            var credentials = new BasicAWSCredentials(accessKeyId, secretAccessKey);
            var config = new AmazonIdentityManagementServiceConfig
            {
                RegionEndpoint = RegionEndpoint.USEast1, // IAM is global, but requires a region
                Timeout = TimeSpan.FromSeconds(10),
                MaxErrorRetry = 2
            };

            return new AmazonIdentityManagementServiceClient(credentials, config);
        }

        /// <summary>
        /// Verifies AWS credentials by calling the STS GetCallerIdentity API.
        /// Tries the primary region (us-east-1) first, then falls back to us-west-2 for non-authentication errors.
        /// </summary>
        /// <param name="accessKeyId">AWS Access Key ID</param>
        /// <param name="secretAccessKey">AWS Secret Access Key</param>
        /// <returns>GetCallerIdentityResponse containing Account ID, ARN, and User ID</returns>
        /// <exception cref="AmazonSecurityTokenServiceException">Thrown when credentials are invalid or authentication fails</exception>
        private async Task<GetCallerIdentityResponse> VerifyCredentialsAsync(
            string accessKeyId,
            string secretAccessKey)
        {
            IAmazonSecurityTokenService? stsClient = null;
            
            try
            {
                // Try primary region (us-east-1)
                stsClient = CreateStsClient(accessKeyId, secretAccessKey, "us-east-1");
                var request = new GetCallerIdentityRequest();
                return await stsClient.GetCallerIdentityAsync(request);
            }
            catch (AmazonSecurityTokenServiceException ex) 
                when (ex.ErrorCode != "InvalidClientTokenId" && 
                      ex.ErrorCode != "SignatureDoesNotMatch")
            {
                // Try fallback region (us-west-2) for non-auth errors
                // Auth errors (InvalidClientTokenId, SignatureDoesNotMatch) indicate invalid credentials
                // and should not be retried in a different region
                _logger?.LogDebug("STS call failed in us-east-1 with {ErrorCode}, retrying with us-west-2 region", ex.ErrorCode);
                
                stsClient?.Dispose();
                stsClient = CreateStsClient(accessKeyId, secretAccessKey, "us-west-2");
                var request = new GetCallerIdentityRequest();
                return await stsClient.GetCallerIdentityAsync(request);
            }
            finally
            {
                stsClient?.Dispose();
            }
        }

        /// <summary>
        /// Enumerates IAM permissions by retrieving attached managed policies for the specified user.
        /// Returns an empty list for root accounts, unknown users, or when permission enumeration fails.
        /// </summary>
        /// <param name="userName">IAM username to enumerate permissions for</param>
        /// <returns>List of attached policy names, or empty list if enumeration fails or is not applicable</returns>
        private async Task<List<string>> EnumeratePermissionsAsync(string userName)
        {
            // Return empty list for root, unknown, or empty usernames
            if (string.IsNullOrEmpty(userName) || userName == "root" || userName == "unknown")
            {
                return new List<string>();
            }

            var policies = new List<string>();

            try
            {
                // Create IAM client if not already created
                // Note: This assumes credentials are available in the current context
                // In practice, this would need to be called with the validated credentials
                if (_iamClient == null)
                {
                    _logger?.LogWarning("IAM client not initialized for permission enumeration");
                    return new List<string>();
                }

                // List attached managed policies
                var request = new ListAttachedUserPoliciesRequest
                {
                    UserName = userName
                };

                var response = await _iamClient.ListAttachedUserPoliciesAsync(request);

                // Extract policy names from response
                foreach (var policy in response.AttachedPolicies)
                {
                    policies.Add(policy.PolicyName);
                }

                return policies;
            }
            catch (AmazonIdentityManagementServiceException ex) when (ex.ErrorCode == "AccessDenied")
            {
                // Handle AccessDenied errors gracefully
                _logger?.LogWarning("Permission enumeration denied for user {UserName}: {ErrorMessage}", 
                    userName, ex.Message);
                
                // Return a special marker to indicate permission enumeration was denied
                return new List<string> { "Permission enumeration denied" };
            }
            catch (Exception ex)
            {
                // Log warnings for other enumeration failures
                _logger?.LogWarning(ex, "Failed to enumerate permissions for user {UserName}", userName);
                return new List<string>();
            }
        }

        /// <summary>
        /// Extracts AWS metadata from the STS GetCallerIdentity API response.
        /// Parses the Account ID, User ARN, User ID, and determines the credential type.
        /// </summary>
        /// <param name="response">GetCallerIdentity API response from AWS STS</param>
        /// <returns>AwsMetadata object containing parsed account information</returns>
        private Task<AwsMetadata> ExtractMetadataAsync(GetCallerIdentityResponse response)
        {
            var metadata = new AwsMetadata
            {
                AccountId = response.Account,
                UserArn = response.Arn,
                UserId = response.UserId
            };

            // Parse ARN to determine credential type
            // ARN formats:
            // - Root: arn:aws:iam::123456789012:root
            // - IAM User: arn:aws:iam::123456789012:user/username
            // - Assumed Role: arn:aws:sts::123456789012:assumed-role/role-name/session-name

            if (metadata.UserArn.Contains(":root"))
            {
                metadata.IsRootAccount = true;
                metadata.CredentialType = "Root Account";
                metadata.UserName = "root";
            }
            else if (metadata.UserArn.Contains(":user/"))
            {
                metadata.CredentialType = "IAM User";
                var parts = metadata.UserArn.Split('/');
                metadata.UserName = parts.Length > 1 ? parts[^1] : "unknown";
            }
            else if (metadata.UserArn.Contains(":assumed-role/"))
            {
                metadata.CredentialType = "Assumed Role";
                var parts = metadata.UserArn.Split('/');
                metadata.UserName = parts.Length > 1 ? parts[1] : "unknown";
            }
            else
            {
                metadata.CredentialType = "Unknown";
                metadata.UserName = "unknown";
            }

            return Task.FromResult(metadata);
        }

        /// <summary>
        /// Calculates the risk level of AWS credentials based on attached policies and account type.
        /// Risk levels: Critical, High, Medium, Low
        /// </summary>
        /// <param name="policies">List of attached IAM policy names</param>
        /// <param name="isRoot">Whether this is a root account credential</param>
        /// <returns>Risk level string: "Critical", "High", "Medium", or "Low"</returns>
        private string CalculateRiskLevel(List<string> policies, bool isRoot)
        {
            // Root account is always critical
            if (isRoot)
            {
                return "Critical";
            }

            // Check for administrator access
            if (policies.Any(p => p.Equals("AdministratorAccess", StringComparison.OrdinalIgnoreCase)))
            {
                return "Critical";
            }

            // Check for power user or full access policies
            var highRiskPatterns = new[]
            {
                "PowerUserAccess",
                "FullAccess",
                "AdminAccess"
            };

            if (policies.Any(p => highRiskPatterns.Any(pattern =>
                p.Contains(pattern, StringComparison.OrdinalIgnoreCase))))
            {
                return "High";
            }

            // Check for write/modify permissions
            var mediumRiskPatterns = new[]
            {
                "Write",
                "Modify",
                "Delete",
                "Create",
                "Put"
            };

            if (policies.Any(p => mediumRiskPatterns.Any(pattern =>
                p.Contains(pattern, StringComparison.OrdinalIgnoreCase))))
            {
                return "Medium";
            }

            // Default to low risk for read-only or limited permissions
            return "Low";
        }

        /// <summary>
        /// Internal class to hold AWS metadata extracted from STS and IAM API responses.
        /// This metadata is used to populate the ValidationResult and ultimately stored in the database.
        /// </summary>
        internal class AwsMetadata
        {
            /// <summary>
            /// 12-digit AWS account identifier
            /// </summary>
            public string AccountId { get; set; } = string.Empty;

            /// <summary>
            /// Amazon Resource Name uniquely identifying the IAM principal
            /// Format examples:
            /// - Root: arn:aws:iam::123456789012:root
            /// - IAM User: arn:aws:iam::123456789012:user/username
            /// - Assumed Role: arn:aws:sts::123456789012:assumed-role/role-name/session-name
            /// </summary>
            public string UserArn { get; set; } = string.Empty;

            /// <summary>
            /// Unique identifier for the IAM principal (from STS GetCallerIdentity)
            /// </summary>
            public string UserId { get; set; } = string.Empty;

            /// <summary>
            /// IAM username extracted from the ARN
            /// For root accounts: "root"
            /// For IAM users: extracted from ARN path
            /// For assumed roles: role name
            /// </summary>
            public string UserName { get; set; } = string.Empty;

            /// <summary>
            /// Type of AWS credential
            /// Values: "Root Account", "IAM User", "Assumed Role", "Unknown"
            /// </summary>
            public string CredentialType { get; set; } = string.Empty;

            /// <summary>
            /// Indicates if this is a root account credential (highest privilege, critical risk)
            /// </summary>
            public bool IsRootAccount { get; set; }

            /// <summary>
            /// List of IAM policy names attached to the user
            /// Empty list if permission enumeration fails or is denied
            /// </summary>
            public List<string> AttachedPolicies { get; set; } = new();
        }
    }
}
