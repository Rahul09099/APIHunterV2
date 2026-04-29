# Design Document: AWS IAM Credential Detection and Verification

## Overview

This design document specifies the technical implementation for adding AWS IAM Access Key detection, verification, and metadata extraction to the UnsecuredAPIKeys tool. The feature will integrate seamlessly with the existing provider architecture while adding AWS-specific capabilities for credential validation, permission enumeration, and risk assessment.

### Goals

1. **Detect AWS IAM credentials** exposed in GitHub repositories using regex patterns
2. **Verify credential validity** using AWS STS GetCallerIdentity API
3. **Extract comprehensive metadata** including Account ID, User ARN, IAM username, and credential type
4. **Enumerate permissions** using AWS IAM APIs to assess risk level
5. **Store AWS-specific data** in the database with 7 new columns
6. **Export AWS metadata** in CSV and JSON formats
7. **Display AWS information** in the CLI with risk-based color coding

### Non-Goals

- AWS CloudTrail integration for usage monitoring
- Automated credential revocation
- AWS Organizations support
- Cross-account permission analysis
- AWS Secrets Manager integration

## Architecture

### High-Level Component Diagram

```mermaid
graph TB
    subgraph "GitHub Search"
        GH[GitHub Code Search API]
    end
    
    subgraph "Scraper Service"
        SS[ScraperService]
        RX[Regex Pattern Matcher]
        CP[Credential Pair Detector]
    end
    
    subgraph "AWS IAM Provider"
        AIP[AWSIAMProvider]
        VAL[Validation Logic]
        META[Metadata Extractor]
        PERM[Permission Enumerator]
        RISK[Risk Assessor]
    end
    
    subgraph "AWS Services"
        STS[AWS STS API<br/>GetCallerIdentity]
        IAM[AWS IAM API<br/>ListAttachedUserPolicies]
    end
    
    subgraph "Database"
        DB[(PostgreSQL/SQLite)]
        APIKEY[APIKeys Table<br/>+ 7 AWS columns]
    end
    
    subgraph "Export & Display"
        EXP[Export Service]
        CLI[CLI Display]
    end
    
    GH -->|Search Results| SS
    SS -->|Regex Match| RX
    RX -->|Credential Pairs| CP
    CP -->|Unverified Keys| AIP
    
    AIP -->|Validate| VAL
    VAL -->|GetCallerIdentity| STS
    STS -->|Account ID, ARN| META
    
    META -->|Extract Metadata| PERM
    PERM -->|ListAttachedUserPolicies| IAM
    IAM -->|Policy List| RISK
    
    RISK -->|Risk Level| APIKEY
    META -->|AWS Metadata| APIKEY
    
    APIKEY -->|Query| EXP
    APIKEY -->|Query| CLI
    
    EXP -->|CSV/JSON| USER[User]
    CLI -->|Terminal Display| USER
```

### Component Interaction Flow

```mermaid
sequenceDiagram
    participant Scraper as ScraperService
    participant Provider as AWSIAMProvider
    participant STS as AWS STS
    participant IAM as AWS IAM
    participant DB as Database
    
    Scraper->>Scraper: Search GitHub for "AKIA"
    Scraper->>Scraper: Find Access Key ID
    Scraper->>Scraper: Search context for Secret Key
    Scraper->>DB: Store unverified credential pair
    
    Provider->>DB: Fetch unverified AWS keys
    Provider->>Provider: Create AWS SDK client
    Provider->>STS: GetCallerIdentity()
    
    alt Valid Credentials
        STS-->>Provider: 200 OK + Account ID + ARN
        Provider->>Provider: Extract Account ID
        Provider->>Provider: Extract User ARN
        Provider->>Provider: Determine credential type
        
        Provider->>IAM: ListAttachedUserPolicies()
        
        alt Permission Granted
            IAM-->>Provider: Policy list
            Provider->>Provider: Calculate risk level
        else Access Denied
            IAM-->>Provider: 403 AccessDenied
            Provider->>Provider: Store "Permission enumeration denied"
        end
        
        Provider->>DB: Update with Valid status + metadata
    else Invalid Credentials
        STS-->>Provider: 403 Forbidden
        Provider->>DB: Update with Invalid status
    else Rate Limited
        STS-->>Provider: 429 Too Many Requests
        Provider->>DB: Update with Valid status (rate limited)
    end
```

## Components and Interfaces

### 1. AWSIAMProvider Class

The `AWSIAMProvider` class inherits from `BaseApiKeyProvider` and implements AWS-specific credential detection and verification logic.

#### Class Structure

```csharp
namespace UnsecuredAPIKeys.Providers.Cloud_Providers
{
    [ApiProvider]
    public class AWSIAMProvider : BaseApiKeyProvider
    {
        // Provider identification
        public override string ProviderName => "AWS IAM";
        public override ApiTypeEnum ApiType => ApiTypeEnum.AWSIAM;
        
        // Regex patterns for detection
        public override IEnumerable<string> RegexPatterns { get; }
        
        // AWS SDK clients
        private IAmazonSecurityTokenService? _stsClient;
        private IAmazonIdentityManagementService? _iamClient;
        
        // Constructors
        public AWSIAMProvider();
        public AWSIAMProvider(ILogger<AWSIAMProvider>? logger);
        
        // Core validation method
        protected override Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, HttpClient httpClient);
        
        // AWS-specific methods
        private Task<GetCallerIdentityResponse> VerifyCredentialsAsync(
            string accessKeyId, string secretAccessKey);
        
        private Task<AwsMetadata> ExtractMetadataAsync(
            GetCallerIdentityResponse response);
        
        private Task<List<string>> EnumeratePermissionsAsync(
            string userName);
        
        private string CalculateRiskLevel(
            List<string> policies, bool isRoot);
        
        private (string accessKeyId, string secretAccessKey) ExtractCredentialPair(
            string apiKey);
    }
}
```

#### Regex Patterns

The provider will use the following regex patterns to detect AWS credentials:

```csharp
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
```

#### Credential Pair Detection Algorithm

Since AWS credentials require both an Access Key ID and Secret Access Key, the provider must implement logic to find matching pairs:

```csharp
private (string accessKeyId, string secretAccessKey) ExtractCredentialPair(string apiKey)
{
    // Case 1: apiKey contains both parts separated by a delimiter
    // Format: "AKIA...:::secret..." or "AKIA...|secret..."
    if (apiKey.Contains(":::") || apiKey.Contains("|"))
    {
        var parts = apiKey.Split(new[] { ":::", "|" }, StringSplitOptions.None);
        if (parts.Length == 2)
        {
            return (parts[0].Trim(), parts[1].Trim());
        }
    }
    
    // Case 2: apiKey is just the Access Key ID
    // The ScraperService will need to search the surrounding code context
    // for the Secret Access Key within 50 lines
    if (apiKey.StartsWith("AKIA") || apiKey.StartsWith("ASIA"))
    {
        // This will be handled by ScraperService context search
        // Return empty secret for now
        return (apiKey, string.Empty);
    }
    
    throw new ArgumentException("Invalid AWS credential format");
}
```

### 2. AWS SDK Integration

#### NuGet Package Dependencies

Add to `UnsecuredAPIKeys.Providers.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="AWSSDK.SecurityToken" Version="3.7.400" />
  <PackageReference Include="AWSSDK.IdentityManagement" Version="3.7.400" />
  <PackageReference Include="AWSSDK.Core" Version="3.7.400" />
</ItemGroup>
```

#### AWS Client Configuration

```csharp
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
```

### 3. Validation Logic Implementation

#### Core Validation Method

```csharp
protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
    string apiKey, 
    HttpClient httpClient)
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
```

#### STS Verification

```csharp
private async Task<GetCallerIdentityResponse> VerifyCredentialsAsync(
    string accessKeyId,
    string secretAccessKey)
{
    // Try primary region (us-east-1)
    try
    {
        _stsClient = CreateStsClient(accessKeyId, secretAccessKey, "us-east-1");
        var request = new GetCallerIdentityRequest();
        return await _stsClient.GetCallerIdentityAsync(request);
    }
    catch (AmazonSecurityTokenServiceException ex) 
        when (ex.ErrorCode != "InvalidClientTokenId" && 
              ex.ErrorCode != "SignatureDoesNotMatch")
    {
        // Try fallback region (us-west-2) for non-auth errors
        _logger?.LogDebug("Retrying with us-west-2 region");
        _stsClient = CreateStsClient(accessKeyId, secretAccessKey, "us-west-2");
        var request = new GetCallerIdentityRequest();
        return await _stsClient.GetCallerIdentityAsync(request);
    }
}
```

### 4. Metadata Extraction

#### Metadata Structure

```csharp
internal class AwsMetadata
{
    public string AccountId { get; set; } = string.Empty;
    public string UserArn { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string CredentialType { get; set; } = string.Empty;
    public bool IsRootAccount { get; set; }
    public List<string> AttachedPolicies { get; set; } = new();
}
```

#### Extraction Algorithm

```csharp
private async Task<AwsMetadata> ExtractMetadataAsync(GetCallerIdentityResponse response)
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
    
    return metadata;
}
```

### 5. Permission Enumeration

#### IAM Policy Retrieval

```csharp
private async Task<List<string>> EnumeratePermissionsAsync(string userName)
{
    if (string.IsNullOrEmpty(userName) || userName == "root" || userName == "unknown")
    {
        return new List<string>();
    }
    
    var policies = new List<string>();
    
    try
    {
        // List attached managed policies
        var request = new ListAttachedUserPoliciesRequest
        {
            UserName = userName
        };
        
        var response = await _iamClient!.ListAttachedUserPoliciesAsync(request);
        
        foreach (var policy in response.AttachedPolicies)
        {
            policies.Add(policy.PolicyName);
        }
        
        return policies;
    }
    catch (AmazonIdentityManagementServiceException ex)
    {
        _logger?.LogWarning("Failed to enumerate permissions: {Error}", ex.Message);
        throw;
    }
}
```

### 6. Risk Assessment Algorithm

```csharp
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
        "AdminAccess",
        "*FullAccess"
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
```

## Data Models

### Database Schema Changes

#### New Columns for APIKeys Table

```sql
-- Add AWS-specific metadata columns to APIKeys table
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsAccountId" TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsUserArn" TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsUserId" TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsCredentialType" TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsAttachedPolicies" TEXT; -- JSON array
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsRiskLevel" TEXT;
ALTER TABLE "APIKeys" ADD COLUMN IF NOT EXISTS "AwsIsRootAccount" BOOLEAN DEFAULT FALSE;

-- Create index for AWS account queries
CREATE INDEX IF NOT EXISTS "IX_APIKeys_AwsAccountId" 
    ON "APIKeys" ("AwsAccountId") 
    WHERE "AwsAccountId" IS NOT NULL;

-- Create index for risk level queries
CREATE INDEX IF NOT EXISTS "IX_APIKeys_AwsRiskLevel" 
    ON "APIKeys" ("AwsRiskLevel") 
    WHERE "AwsRiskLevel" IS NOT NULL;
```

#### Updated APIKey Model

```csharp
namespace UnsecuredAPIKeys.Data.Models
{
    public class APIKey
    {
        // ... existing properties ...
        
        // AWS-specific metadata
        public string? AwsAccountId { get; set; }
        public string? AwsUserArn { get; set; }
        public string? AwsUserId { get; set; }
        public string? AwsCredentialType { get; set; }
        public string? AwsAttachedPolicies { get; set; } // JSON serialized List<string>
        public string? AwsRiskLevel { get; set; }
        public bool AwsIsRootAccount { get; set; }
    }
}
```

#### ValidationResult Extensions

```csharp
namespace UnsecuredAPIKeys.Providers.Common
{
    public class ValidationResult
    {
        // ... existing properties ...
        
        // AWS-specific properties
        public string? AwsAccountId { get; set; }
        public string? AwsUserArn { get; set; }
        public string? AwsUserId { get; set; }
        public string? AwsCredentialType { get; set; }
        public List<string>? AwsAttachedPolicies { get; set; }
        public string? AwsRiskLevel { get; set; }
        public bool AwsIsRootAccount { get; set; }
    }
}
```

### Enum Updates

#### ApiTypeEnum Addition

```csharp
public enum ApiTypeEnum
{
    // ... existing values ...
    
    AzureOpenAI = 320,
    AWSIAM = 330,  // <-- New value
    
    // Communication Category (4)
    SendGrid = 410,
    // ...
}
```

## Error Handling

### AWS-Specific Error Scenarios

#### 1. Invalid Credentials

```csharp
// Error Code: InvalidClientTokenId
// HTTP Status: 403
// Action: Mark as Invalid
catch (AmazonSecurityTokenServiceException ex) 
    when (ex.ErrorCode == "InvalidClientTokenId")
{
    return ValidationResult.IsUnauthorized(HttpStatusCode.Forbidden);
}
```

#### 2. Signature Mismatch

```csharp
// Error Code: SignatureDoesNotMatch
// HTTP Status: 403
// Action: Mark as Invalid (wrong secret key)
catch (AmazonSecurityTokenServiceException ex) 
    when (ex.ErrorCode == "SignatureDoesNotMatch")
{
    return ValidationResult.IsUnauthorized(HttpStatusCode.Forbidden);
}
```

#### 3. Rate Limiting

```csharp
// Error Code: Throttling
// HTTP Status: 429
// Action: Mark as Valid (authenticated but rate limited)
catch (AmazonServiceException ex) when ((int)ex.StatusCode == 429)
{
    return ValidationResult.Success(
        (HttpStatusCode)429,
        "Rate limited (key is valid)");
}
```

#### 4. Permission Denied (IAM Enumeration)

```csharp
// Error Code: AccessDenied
// HTTP Status: 403
// Action: Continue with partial metadata
catch (AmazonIdentityManagementServiceException ex) 
    when (ex.ErrorCode == "AccessDenied")
{
    _logger?.LogDebug("Permission enumeration denied");
    metadata.AttachedPolicies = new List<string> { "Permission enumeration denied" };
    // Continue processing
}
```

#### 5. Network Errors

```csharp
// Timeout, DNS failure, connection refused
// Action: Increment error count, retry with exponential backoff
catch (HttpRequestException ex)
{
    _logger?.LogWarning(ex, "Network error during AWS validation");
    return ValidationResult.HasNetworkError($"Network error: {ex.Message}");
}
```

#### 6. Region-Specific Errors

```csharp
// Some AWS services may be unavailable in certain regions
// Action: Retry with fallback region (us-west-2)
catch (AmazonSecurityTokenServiceException ex) 
    when (ex.ErrorCode == "ServiceUnavailable")
{
    _logger?.LogDebug("Service unavailable in us-east-1, trying us-west-2");
    // Retry logic with fallback region
}
```

### Error Handling Flow

```mermaid
graph TD
    START[Validate AWS Credentials] --> EXTRACT[Extract Access Key + Secret]
    EXTRACT --> CHECK{Secret Key Found?}
    
    CHECK -->|No| ERR1[Return: Provider Specific Error]
    CHECK -->|Yes| STS[Call STS GetCallerIdentity]
    
    STS --> STSERR{STS Error?}
    STSERR -->|InvalidClientTokenId| ERR2[Return: Unauthorized]
    STSERR -->|SignatureDoesNotMatch| ERR3[Return: Unauthorized]
    STSERR -->|Throttling 429| SUCCESS1[Return: Valid Rate Limited]
    STSERR -->|Network Error| ERR4[Return: Network Error]
    STSERR -->|No Error| META[Extract Metadata]
    
    META --> IAM[Call IAM ListAttachedUserPolicies]
    IAM --> IAMERR{IAM Error?}
    
    IAMERR -->|AccessDenied| PARTIAL[Store Partial Metadata]
    IAMERR -->|No Error| FULL[Store Full Metadata]
    IAMERR -->|Network Error| PARTIAL
    
    PARTIAL --> RISK[Calculate Risk Level]
    FULL --> RISK
    
    RISK --> SUCCESS2[Return: Valid + Metadata]
```

## Testing Strategy

### Unit Tests

Unit tests will verify specific behaviors and edge cases:

1. **Credential Pair Extraction**
   - Test extraction from delimited format (`AKIA...:::secret...`)
   - Test extraction from environment variable format
   - Test handling of malformed credentials

2. **ARN Parsing**
   - Test root account detection (`arn:aws:iam::123456789012:root`)
   - Test IAM user extraction (`arn:aws:iam::123456789012:user/john`)
   - Test assumed role extraction (`arn:aws:sts::123456789012:assumed-role/MyRole/session`)

3. **Risk Level Calculation**
   - Test Critical: Root account
   - Test Critical: AdministratorAccess policy
   - Test High: PowerUserAccess policy
   - Test Medium: Write permissions
   - Test Low: Read-only permissions

4. **Error Handling**
   - Test InvalidClientTokenId response
   - Test SignatureDoesNotMatch response
   - Test Throttling (429) response
   - Test AccessDenied during permission enumeration

### Integration Tests

Integration tests will use mocked AWS SDK clients:

1. **STS GetCallerIdentity Mock**
   - Mock successful response with valid ARN
   - Mock 403 Forbidden response
   - Mock 429 rate limit response

2. **IAM ListAttachedUserPolicies Mock**
   - Mock successful policy list response
   - Mock AccessDenied response
   - Mock empty policy list

3. **End-to-End Validation Flow**
   - Mock complete validation with metadata extraction
   - Mock validation with permission enumeration failure
   - Mock validation with network errors

### Test Data

```csharp
// Valid test credentials (mocked responses)
public static class TestData
{
    public const string ValidAccessKeyId = "AKIAIOSFODNN7EXAMPLE";
    public const string ValidSecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    
    public const string RootArn = "arn:aws:iam::123456789012:root";
    public const string UserArn = "arn:aws:iam::123456789012:user/testuser";
    public const string RoleArn = "arn:aws:sts::123456789012:assumed-role/TestRole/session";
    
    public static List<string> AdminPolicies = new() { "AdministratorAccess" };
    public static List<string> PowerUserPolicies = new() { "PowerUserAccess" };
    public static List<string> ReadOnlyPolicies = new() { "ReadOnlyAccess" };
}
```

## Export Service Modifications

### CSV Export Updates

```csharp
private async Task ExportAsCsvAsync(List<APIKey> keys, string filePath)
{
    var lines = new List<string>
    {
        // Updated header with AWS columns
        "Id,ApiKey,Type,TypeName,Status,StatusName,Balance,Tier,ValidationResponse," +
        "AwsAccountId,AwsUserArn,AwsCredentialType,AwsRiskLevel,AwsIsRootAccount,AwsAttachedPolicies," +
        "FirstFoundUTC,LastCheckedUTC,Source,SourceFoundUTC"
    };

    foreach (var key in keys)
    {
        var valResponse = key.ValidationResponse?
            .Replace("\"", "\"\"")
            .Replace("\n", " ")
            .Replace("\r", " ") ?? "";
        
        // Format AWS attached policies as comma-separated string
        var policies = string.Empty;
        if (!string.IsNullOrEmpty(key.AwsAttachedPolicies))
        {
            try
            {
                var policyList = JsonSerializer.Deserialize<List<string>>(key.AwsAttachedPolicies);
                policies = policyList != null ? string.Join("; ", policyList) : "";
            }
            catch
            {
                policies = key.AwsAttachedPolicies;
            }
        }
        
        if (key.References == null || !key.References.Any())
        {
            lines.Add($"{key.Id},\"{key.ApiKey}\",{(int)key.ApiType},{key.ApiType}," +
                     $"{(int)key.Status},{key.Status},\"{key.Balance}\",\"{key.AccountTier}\"," +
                     $"\"{valResponse}\"," +
                     $"\"{key.AwsAccountId}\",\"{key.AwsUserArn}\",\"{key.AwsCredentialType}\"," +
                     $"\"{key.AwsRiskLevel}\",{key.AwsIsRootAccount},\"{policies}\"," +
                     $"{key.FirstFoundUTC:O},{key.LastCheckedUTC:O},\"\",");
        }
        else
        {
            foreach (var r in key.References)
            {
                var source = r.FileURL ?? 
                    (string.IsNullOrWhiteSpace(r.RepoURL) ? "" : 
                     $"{r.RepoURL}/blob/{r.Branch ?? "main"}/{r.FilePath}");
                
                lines.Add($"{key.Id},\"{key.ApiKey}\",{(int)key.ApiType},{key.ApiType}," +
                         $"{(int)key.Status},{key.Status},\"{key.Balance}\",\"{key.AccountTier}\"," +
                         $"\"{valResponse}\"," +
                         $"\"{key.AwsAccountId}\",\"{key.AwsUserArn}\",\"{key.AwsCredentialType}\"," +
                         $"\"{key.AwsRiskLevel}\",{key.AwsIsRootAccount},\"{policies}\"," +
                         $"{key.FirstFoundUTC:O},{key.LastCheckedUTC:O},\"{source}\",{r.FoundUTC:O}");
            }
        }
    }

    await File.WriteAllLinesAsync(filePath, lines);
}
```

### JSON Export Updates

```csharp
private async Task ExportAsJsonAsync(List<APIKey> keys, string filePath)
{
    var exportData = keys.Select(k => new
    {
        k.Id,
        k.ApiKey,
        ApiType = k.ApiType.ToString(),
        ApiTypeCode = (int)k.ApiType,
        Status = k.Status.ToString(),
        StatusCode = (int)k.Status,
        k.Balance,
        k.AccountTier,
        k.ValidationResponse,
        
        // AWS metadata
        AwsMetadata = k.AwsAccountId != null ? new
        {
            k.AwsAccountId,
            k.AwsUserArn,
            k.AwsCredentialType,
            k.AwsRiskLevel,
            k.AwsIsRootAccount,
            AwsAttachedPolicies = !string.IsNullOrEmpty(k.AwsAttachedPolicies) 
                ? JsonSerializer.Deserialize<List<string>>(k.AwsAttachedPolicies)
                : new List<string>()
        } : null,
        
        k.FirstFoundUTC,
        k.LastCheckedUTC,
        References = k.References?.Select(r => new
        {
            r.RepoURL,
            r.RepoOwner,
            r.RepoName,
            r.FileURL,
            r.FilePath,
            r.FoundUTC
        }).ToList()
    }).ToList();

    var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    await File.WriteAllTextAsync(filePath, json);
}
```

## CLI Display Updates

### Display AWS Metadata in Key Details

```csharp
private static void DisplayKeyDetails(APIKey key)
{
    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[bold]Property[/]")
        .AddColumn("[bold]Value[/]");

    table.AddRow("ID", key.Id.ToString());
    table.AddRow("API Key", $"[dim]{Markup.Escape(MaskKey(key.ApiKey))}[/]");
    table.AddRow("Type", $"[cyan]{key.ApiType}[/]");
    table.AddRow("Status", GetStatusMarkup(key.Status));
    
    if (!string.IsNullOrEmpty(key.Balance))
        table.AddRow("Balance", $"[green]{Markup.Escape(key.Balance)}[/]");
    
    if (!string.IsNullOrEmpty(key.AccountTier))
        table.AddRow("Tier", $"[yellow]{Markup.Escape(key.AccountTier)}[/]");
    
    // AWS-specific metadata
    if (!string.IsNullOrEmpty(key.AwsAccountId))
    {
        table.AddRow("[bold cyan]AWS Account ID[/]", 
            $"[cyan]{Markup.Escape(key.AwsAccountId)}[/]");
    }
    
    if (!string.IsNullOrEmpty(key.AwsUserArn))
    {
        table.AddRow("[bold cyan]AWS User ARN[/]", 
            $"[dim]{Markup.Escape(key.AwsUserArn)}[/]");
    }
    
    if (!string.IsNullOrEmpty(key.AwsCredentialType))
    {
        table.AddRow("[bold cyan]AWS Credential Type[/]", 
            $"[yellow]{Markup.Escape(key.AwsCredentialType)}[/]");
    }
    
    if (!string.IsNullOrEmpty(key.AwsRiskLevel))
    {
        var riskColor = key.AwsRiskLevel switch
        {
            "Critical" => "red",
            "High" => "orange1",
            "Medium" => "yellow",
            "Low" => "green",
            _ => "white"
        };
        table.AddRow("[bold cyan]AWS Risk Level[/]", 
            $"[{riskColor}]{Markup.Escape(key.AwsRiskLevel)}[/]");
    }
    
    if (key.AwsIsRootAccount)
    {
        table.AddRow("[bold red]⚠️ ROOT ACCOUNT[/]", 
            "[red]CRITICAL SECURITY RISK[/]");
    }
    
    if (!string.IsNullOrEmpty(key.AwsAttachedPolicies))
    {
        try
        {
            var policies = JsonSerializer.Deserialize<List<string>>(key.AwsAttachedPolicies);
            if (policies != null && policies.Any())
            {
                var policyText = string.Join("\n", policies.Select(p => $"  • {p}"));
                table.AddRow("[bold cyan]AWS Attached Policies[/]", 
                    $"[dim]{Markup.Escape(policyText)}[/]");
            }
        }
        catch
        {
            table.AddRow("[bold cyan]AWS Attached Policies[/]", 
                $"[dim]{Markup.Escape(key.AwsAttachedPolicies)}[/]");
        }
    }
    
    if (!string.IsNullOrEmpty(key.ValidationResponse))
        table.AddRow("Validation", $"[dim]{Markup.Escape(key.ValidationResponse)}[/]");
    
    table.AddRow("First Found", key.FirstFoundUTC.ToString("yyyy-MM-dd HH:mm:ss UTC"));
    
    if (key.LastCheckedUTC.HasValue)
        table.AddRow("Last Checked", key.LastCheckedUTC.Value.ToString("yyyy-MM-dd HH:mm:ss UTC"));

    AnsiConsole.Write(table);
}

private static string GetStatusMarkup(ApiStatusEnum status)
{
    return status switch
    {
        ApiStatusEnum.Valid => "[green]Valid[/]",
        ApiStatusEnum.ValidNoCredits => "[yellow]Valid (No Credits)[/]",
        ApiStatusEnum.Invalid => "[red]Invalid[/]",
        ApiStatusEnum.Unverified => "[grey]Unverified[/]",
        ApiStatusEnum.Error => "[orange1]Error[/]",
        _ => "[white]Unknown[/]"
    };
}
```

### Display AWS Keys in List View

```csharp
private static void DisplayValidKeys(List<APIKey> keys)
{
    var table = new Table()
        .Border(TableBorder.Rounded)
        .AddColumn("[bold]ID[/]")
        .AddColumn("[bold]Type[/]")
        .AddColumn("[bold]Key[/]")
        .AddColumn("[bold]Status[/]")
        .AddColumn("[bold]AWS Account[/]")
        .AddColumn("[bold]Risk[/]");

    foreach (var key in keys)
    {
        var maskedKey = MaskKey(key.ApiKey);
        var statusMarkup = GetStatusMarkup(key.Status);
        
        var awsAccount = !string.IsNullOrEmpty(key.AwsAccountId) 
            ? key.AwsAccountId 
            : "N/A";
        
        var riskMarkup = "N/A";
        if (!string.IsNullOrEmpty(key.AwsRiskLevel))
        {
            riskMarkup = key.AwsRiskLevel switch
            {
                "Critical" => "[red]Critical[/]",
                "High" => "[orange1]High[/]",
                "Medium" => "[yellow]Medium[/]",
                "Low" => "[green]Low[/]",
                _ => "N/A"
            };
        }
        
        table.AddRow(
            key.Id.ToString(),
            $"[cyan]{key.ApiType}[/]",
            $"[dim]{Markup.Escape(maskedKey)}[/]",
            statusMarkup,
            $"[cyan]{Markup.Escape(awsAccount)}[/]",
            riskMarkup
        );
    }

    AnsiConsole.Write(table);
}
```

## Search Query Integration

### Default Search Queries

Add AWS-specific search queries to `DatabaseService.SeedDefaultDataAsync`:

```csharp
var defaultQueries = new[]
{
    // ... existing queries ...
    
    // AWS IAM queries
    "AKIA",
    "ASIA",
    "AWS_ACCESS_KEY_ID",
    "AWS_SECRET_ACCESS_KEY",
    "aws_access_key_id",
    "aws_secret_access_key",
    "aws:access_key_id",
    "aws:secret_access_key"
};
```

### SQL Migration for Search Queries

```sql
-- Add AWS IAM search queries (idempotent)
INSERT INTO "SearchQueries" ("Query", "IsEnabled", "LastSearchUTC")
SELECT v.q, TRUE, CURRENT_TIMESTAMP
FROM (VALUES
    ('AKIA'),
    ('ASIA'),
    ('AWS_ACCESS_KEY_ID'),
    ('AWS_SECRET_ACCESS_KEY'),
    ('aws_access_key_id'),
    ('aws_secret_access_key')
) AS v(q)
WHERE NOT EXISTS (
    SELECT 1 FROM "SearchQueries" WHERE "Query" = v.q
);
```

### Scraper Service Integration

Update `ScraperService.InferProviderFromQuery`:

```csharp
private static string InferProviderFromQuery(string query)
{
    var q = query.ToLowerInvariant();
    
    // ... existing provider checks ...
    
    if (q.Contains("akia") || q.Contains("asia") || q.Contains("aws"))
        return "AWS IAM";
    
    return "Unknown";
}
```

## Rate Limiting Configuration

### Provider Rate Limits

Add AWS IAM to `ProviderRateLimits` in `Constants.cs`:

```csharp
public static class ProviderRateLimits
{
    public const int OpenAI = 5;
    public const int Anthropic = 3;
    public const int Google = 5;
    public const int DeepSeek = 3;
    public const int Groq = 3;
    public const int Mistral = 3;
    public const int OpenRouter = 3;
    public const int Perplexity = 3;
    public const int Cerebras = 3;
    public const int VoyageAI = 3;
    public const int AWSBedrock = 3;
    public const int AzureOpenAI = 3;
    public const int AWSIAM = 3;  // <-- New limit
    public const int Default = 3;
}
```

### Rate Limiter Integration

Update `ProviderRateLimiter.GetSemaphore` in `VerifierService.cs`:

```csharp
public static SemaphoreSlim GetSemaphore(string providerName)
{
    return _semaphores.GetOrAdd(providerName, name =>
    {
        int limit = name switch
        {
            "OpenAI"       => ProviderRateLimits.OpenAI,
            "Anthropic"    => ProviderRateLimits.Anthropic,
            "Google"       => ProviderRateLimits.Google,
            "DeepSeek"     => ProviderRateLimits.DeepSeek,
            "Groq"         => ProviderRateLimits.Groq,
            "Mistral AI"   => ProviderRateLimits.Mistral,
            "OpenRouter"   => ProviderRateLimits.OpenRouter,
            "Perplexity"   => ProviderRateLimits.Perplexity,
            "Cerebras"     => ProviderRateLimits.Cerebras,
            "Voyage AI"    => ProviderRateLimits.VoyageAI,
            "AWS Bedrock"  => ProviderRateLimits.AWSBedrock,
            "Azure OpenAI" => ProviderRateLimits.AzureOpenAI,
            "AWS IAM"      => ProviderRateLimits.AWSIAM,  // <-- New case
            _              => ProviderRateLimits.Default
        };
        return new SemaphoreSlim(limit, limit);
    });
}
```

## Implementation Checklist

### Phase 1: Core Provider Implementation
- [ ] Add `AWSIAM = 330` to `ApiTypeEnum` in `CommonEnums.cs`
- [ ] Create `AWSIAMProvider.cs` in `UnsecuredAPIKeys.Providers/Cloud Providers/`
- [ ] Implement regex patterns for AWS credential detection
- [ ] Implement credential pair extraction logic
- [ ] Add AWS SDK NuGet packages to `UnsecuredAPIKeys.Providers.csproj`
- [ ] Implement STS client creation and configuration
- [ ] Implement IAM client creation and configuration
- [ ] Implement `ValidateKeyWithHttpClientAsync` method
- [ ] Implement `VerifyCredentialsAsync` with region fallback
- [ ] Implement `ExtractMetadataAsync` with ARN parsing
- [ ] Implement `EnumeratePermissionsAsync` with error handling
- [ ] Implement `CalculateRiskLevel` algorithm
- [ ] Add `[ApiProvider]` attribute for auto-discovery

### Phase 2: Database Schema
- [ ] Add 7 AWS columns to `APIKey` model in `APIKey.cs`
- [ ] Add AWS properties to `ValidationResult` in `ValidationResult.cs`
- [ ] Update `DBContext.cs` with AWS column mappings
- [ ] Create database migration script in `master_init.sql`
- [ ] Add indexes for `AwsAccountId` and `AwsRiskLevel`
- [ ] Test schema changes on SQLite
- [ ] Test schema changes on PostgreSQL

### Phase 3: Export Service
- [ ] Update `ExportAsCsvAsync` to include AWS columns
- [ ] Update `ExportAsJsonAsync` to include AWS metadata object
- [ ] Implement AWS attached policies formatting for CSV
- [ ] Test CSV export with AWS keys
- [ ] Test JSON export with AWS keys
- [ ] Test export with null AWS metadata

### Phase 4: CLI Display
- [ ] Update `DisplayKeyDetails` to show AWS metadata
- [ ] Update `DisplayValidKeys` to show AWS account and risk
- [ ] Implement risk level color coding
- [ ] Implement root account warning display
- [ ] Test CLI display with various AWS credential types
- [ ] Test CLI display with different risk levels

### Phase 5: Search Integration
- [ ] Add AWS search queries to `SeedDefaultDataAsync`
- [ ] Update `master_init.sql` with AWS query inserts
- [ ] Update `InferProviderFromQuery` to recognize AWS patterns
- [ ] Test scraper with AWS queries
- [ ] Verify credential pair detection in context

### Phase 6: Rate Limiting
- [ ] Add `AWSIAM = 3` to `ProviderRateLimits`
- [ ] Update `ProviderRateLimiter.GetSemaphore` with AWS IAM case
- [ ] Test rate limiting with concurrent AWS validations

### Phase 7: Testing
- [ ] Write unit tests for credential pair extraction
- [ ] Write unit tests for ARN parsing
- [ ] Write unit tests for risk level calculation
- [ ] Write integration tests with mocked AWS SDK
- [ ] Write end-to-end tests for validation flow
- [ ] Test error handling scenarios
- [ ] Test region fallback logic

### Phase 8: Documentation
- [ ] Update `DEVELOPER_GUIDE.md` with AWS IAM provider example
- [ ] Update `README.md` with AWS IAM support
- [ ] Add AWS IAM to provider list in documentation
- [ ] Document AWS-specific configuration options

## Security Considerations

### Credential Storage

- AWS credentials are stored in the database with the same security as other API keys
- The database should be encrypted at rest
- Access to the database should be restricted
- Consider implementing credential masking in logs

### Permission Enumeration

- Permission enumeration may fail if the credential lacks IAM read permissions
- This is expected behavior and should not block validation
- Store "Permission enumeration denied" in metadata when this occurs

### Rate Limiting

- AWS STS has rate limits (varies by region and account)
- Implement exponential backoff for rate limit errors
- Use per-provider semaphore to limit concurrent requests
- Consider implementing a global AWS rate limiter if needed

### Root Account Detection

- Root account credentials are the highest risk
- Always flag root accounts as "Critical" risk
- Display prominent warnings in CLI for root accounts
- Consider implementing alerts for root account discoveries

## Performance Considerations

### Credential Pair Detection

- Searching for secret keys in code context is expensive
- Limit context search to 50 lines before/after access key
- Use compiled regex patterns for performance
- Consider caching context search results

### AWS API Calls

- STS GetCallerIdentity is fast (~100-200ms)
- IAM ListAttachedUserPolicies is slower (~300-500ms)
- Implement timeouts (10 seconds) for all AWS calls
- Use region fallback only for non-auth errors

### Database Queries

- Add indexes for `AwsAccountId` and `AwsRiskLevel`
- Use JSON serialization for `AwsAttachedPolicies`
- Consider denormalizing frequently queried AWS fields

### Export Performance

- CSV export with AWS columns adds ~10% overhead
- JSON export with nested AWS object adds ~15% overhead
- Consider streaming exports for large datasets

## Future Enhancements

### Phase 2 Features (Not in Current Scope)

1. **AWS CloudTrail Integration**
   - Monitor credential usage in CloudTrail logs
   - Detect active vs. inactive credentials
   - Track last usage timestamp

2. **Automated Revocation**
   - Integrate with AWS IAM to revoke discovered credentials
   - Require user confirmation before revocation
   - Log revocation actions

3. **AWS Organizations Support**
   - Detect organization-level credentials
   - Map credentials to organizational units
   - Assess cross-account permissions

4. **Enhanced Permission Analysis**
   - Parse inline policies in addition to managed policies
   - Analyze policy documents for specific permissions
   - Detect overly permissive policies

5. **AWS Secrets Manager Integration**
   - Check if credentials are stored in Secrets Manager
   - Detect secrets that should be rotated
   - Integrate with automatic rotation

6. **Compliance Reporting**
   - Generate compliance reports for discovered credentials
   - Map to security frameworks (CIS, NIST)
   - Export audit trails

## Appendix

### AWS API Reference

- **STS GetCallerIdentity**: https://docs.aws.amazon.com/STS/latest/APIReference/API_GetCallerIdentity.html
- **IAM ListAttachedUserPolicies**: https://docs.aws.amazon.com/IAM/latest/APIReference/API_ListAttachedUserPolicies.html
- **AWS SDK for .NET**: https://docs.aws.amazon.com/sdk-for-net/

### AWS Credential Formats

- **Access Key ID**: 20 characters, starts with `AKIA` (long-term) or `ASIA` (temporary)
- **Secret Access Key**: 40 characters, base64-encoded
- **Session Token**: Variable length, only for temporary credentials (ASIA)

### AWS ARN Formats

- **Root**: `arn:aws:iam::123456789012:root`
- **IAM User**: `arn:aws:iam::123456789012:user/username`
- **Assumed Role**: `arn:aws:sts::123456789012:assumed-role/role-name/session-name`
- **Federated User**: `arn:aws:sts::123456789012:federated-user/user-name`

### AWS Error Codes

- **InvalidClientTokenId**: Access Key ID not found
- **SignatureDoesNotMatch**: Secret Access Key is incorrect
- **AccessDenied**: Insufficient permissions
- **Throttling**: Rate limit exceeded
- **ServiceUnavailable**: AWS service temporarily unavailable

### Risk Level Matrix

| Condition | Risk Level |
|-----------|-----------|
| Root account | Critical |
| AdministratorAccess policy | Critical |
| PowerUserAccess or *FullAccess | High |
| Write/Modify/Delete permissions | Medium |
| Read-only permissions | Low |
| No policies attached | Low |
