# Requirements Document: AWS IAM Credential Detection and Verification

## Introduction

This feature adds comprehensive AWS IAM Access Key detection, verification, and metadata extraction to the UnsecuredAPIKeys tool. The system will detect exposed AWS IAM credentials on GitHub, verify their validity using AWS STS (Security Token Service), enumerate permissions, and extract detailed metadata including Account ID, User ARN, attached policies, and risk assessment. All metadata will be stored in the database and included in export functionality.

## Glossary

- **AWS_IAM_Provider**: The provider class responsible for detecting and verifying AWS IAM credentials
- **Access_Key_ID**: AWS IAM long-term credential identifier starting with AKIA (20 characters)
- **Secret_Access_Key**: AWS IAM secret credential (40 characters, base64-encoded)
- **Session_Token**: AWS temporary credential identifier starting with ASIA (20 characters)
- **STS_Service**: AWS Security Token Service used for credential verification via GetCallerIdentity API
- **IAM_Service**: AWS Identity and Access Management service used for permission enumeration
- **User_ARN**: Amazon Resource Name uniquely identifying an IAM user (format: arn:aws:iam::account-id:user/username)
- **Account_ID**: 12-digit AWS account identifier
- **Attached_Policy**: IAM policy document granting permissions to the credential
- **Root_Account**: AWS account root user credentials (highest privilege, critical security risk)
- **Database_Context**: Entity Framework DBContext for storing API key metadata
- **Export_Service**: Service responsible for exporting API keys with metadata to CSV/JSON formats
- **Scraper_Service**: Service that searches GitHub for exposed credentials using regex patterns
- **Verifier_Service**: Service that validates discovered credentials and extracts metadata

## Requirements

### Requirement 1: Detect AWS IAM Access Keys

**User Story:** As a security researcher, I want to detect exposed AWS IAM Access Keys in GitHub repositories, so that I can identify compromised credentials.

#### Acceptance Criteria

1. WHEN a GitHub file contains an AWS Access Key ID pattern (AKIA[0-9A-Z]{16}), THE AWS_IAM_Provider SHALL extract the Access Key ID
2. WHEN a GitHub file contains an AWS Session Token pattern (ASIA[0-9A-Z]{16}), THE AWS_IAM_Provider SHALL extract the Session Token
3. WHEN an Access Key ID is found, THE AWS_IAM_Provider SHALL search the surrounding code context (within 50 lines) for the corresponding Secret Access Key (40 alphanumeric characters)
4. WHEN both Access Key ID and Secret Access Key are found in proximity, THE AWS_IAM_Provider SHALL create a credential pair record
5. THE AWS_IAM_Provider SHALL use regex patterns compatible with the existing provider pattern system
6. THE AWS_IAM_Provider SHALL inherit from BaseApiKeyProvider
7. THE AWS_IAM_Provider SHALL use the [ApiProvider] attribute for auto-discovery
8. THE Scraper_Service SHALL add AWS IAM search queries to the default query list

### Requirement 2: Verify AWS IAM Credentials

**User Story:** As a security researcher, I want to verify if discovered AWS credentials are valid, so that I can prioritize active security risks.

#### Acceptance Criteria

1. WHEN AWS credentials are discovered, THE AWS_IAM_Provider SHALL call the AWS STS GetCallerIdentity API to verify validity
2. WHEN the STS API returns HTTP 200, THE AWS_IAM_Provider SHALL mark the credential as Valid
3. WHEN the STS API returns HTTP 403 or 401, THE AWS_IAM_Provider SHALL mark the credential as Invalid
4. WHEN the STS API returns HTTP 429, THE AWS_IAM_Provider SHALL mark the credential as Valid (rate limited but authenticated)
5. WHEN network errors occur during verification, THE AWS_IAM_Provider SHALL increment the error count and retry according to BaseApiKeyProvider retry logic
6. THE AWS_IAM_Provider SHALL handle AWS SDK authentication using the discovered Access Key ID and Secret Access Key
7. THE AWS_IAM_Provider SHALL use the us-east-1 region as the primary verification endpoint
8. WHEN verification fails in us-east-1, THE AWS_IAM_Provider SHALL retry using us-west-2 as a fallback region

### Requirement 3: Extract AWS Account Metadata

**User Story:** As a security researcher, I want to extract AWS account information from valid credentials, so that I can understand the scope of exposure.

#### Acceptance Criteria

1. WHEN the STS GetCallerIdentity API returns successfully, THE AWS_IAM_Provider SHALL extract the Account ID from the response
2. WHEN the STS GetCallerIdentity API returns successfully, THE AWS_IAM_Provider SHALL extract the User ARN from the response
3. WHEN the STS GetCallerIdentity API returns successfully, THE AWS_IAM_Provider SHALL extract the User ID from the response
4. WHEN the User ARN contains ":root", THE AWS_IAM_Provider SHALL flag the credential as a Root Account credential
5. WHEN the User ARN contains ":user/", THE AWS_IAM_Provider SHALL extract the IAM username
6. WHEN the User ARN contains ":assumed-role/", THE AWS_IAM_Provider SHALL extract the role name
7. THE AWS_IAM_Provider SHALL store the Account ID in the database
8. THE AWS_IAM_Provider SHALL store the User ARN in the database
9. THE AWS_IAM_Provider SHALL store the credential type (IAM user, role, root) in the database

### Requirement 4: Enumerate IAM Permissions

**User Story:** As a security researcher, I want to enumerate the permissions attached to discovered credentials, so that I can assess the risk level.

#### Acceptance Criteria

1. WHEN credentials are verified as valid, THE AWS_IAM_Provider SHALL call the IAM ListAttachedUserPolicies API to retrieve attached policies
2. WHEN the IAM API returns policy information, THE AWS_IAM_Provider SHALL extract policy names
3. WHEN the IAM API returns policy information, THE AWS_IAM_Provider SHALL extract policy ARNs
4. WHEN a policy named "AdministratorAccess" is attached, THE AWS_IAM_Provider SHALL flag the credential as having admin access
5. WHEN policies contain "FullAccess" in the name, THE AWS_IAM_Provider SHALL flag the credential as having elevated permissions
6. WHEN the IAM API returns AccessDenied errors, THE AWS_IAM_Provider SHALL store "Permission enumeration denied" in the metadata
7. THE AWS_IAM_Provider SHALL store the list of attached policy names in the database
8. THE AWS_IAM_Provider SHALL calculate a risk level (Critical, High, Medium, Low) based on attached permissions

### Requirement 5: Store AWS Metadata in Database

**User Story:** As a developer, I want AWS metadata stored in the database, so that it persists across application restarts and can be queried.

#### Acceptance Criteria

1. THE Database_Context SHALL add an "AwsAccountId" column to the APIKeys table (TEXT type, nullable)
2. THE Database_Context SHALL add an "AwsUserArn" column to the APIKeys table (TEXT type, nullable)
3. THE Database_Context SHALL add an "AwsUserId" column to the APIKeys table (TEXT type, nullable)
4. THE Database_Context SHALL add an "AwsCredentialType" column to the APIKeys table (TEXT type, nullable)
5. THE Database_Context SHALL add an "AwsAttachedPolicies" column to the APIKeys table (TEXT type, nullable, stores JSON array)
6. THE Database_Context SHALL add an "AwsRiskLevel" column to the APIKeys table (TEXT type, nullable)
7. THE Database_Context SHALL add an "AwsIsRootAccount" column to the APIKeys table (BOOLEAN type, default FALSE)
8. WHEN AWS credentials are verified, THE AWS_IAM_Provider SHALL populate all AWS-specific columns
9. THE master_init.sql script SHALL include ALTER TABLE statements to add AWS columns idempotently

### Requirement 6: Export AWS Metadata

**User Story:** As a security researcher, I want to export discovered AWS credentials with all metadata, so that I can analyze and report findings.

#### Acceptance Criteria

1. WHEN exporting to CSV format, THE Export_Service SHALL include the AwsAccountId column
2. WHEN exporting to CSV format, THE Export_Service SHALL include the AwsUserArn column
3. WHEN exporting to CSV format, THE Export_Service SHALL include the AwsCredentialType column
4. WHEN exporting to CSV format, THE Export_Service SHALL include the AwsAttachedPolicies column
5. WHEN exporting to CSV format, THE Export_Service SHALL include the AwsRiskLevel column
6. WHEN exporting to CSV format, THE Export_Service SHALL include the AwsIsRootAccount column
7. WHEN exporting to JSON format, THE Export_Service SHALL include all AWS metadata fields as nested objects
8. WHEN AWS metadata is null, THE Export_Service SHALL export empty strings for CSV and null values for JSON
9. THE Export_Service SHALL format the AwsAttachedPolicies JSON array as a comma-separated string for CSV export

### Requirement 7: Integrate with Existing Provider System

**User Story:** As a developer, I want AWS IAM detection to integrate seamlessly with the existing provider architecture, so that it requires minimal code changes.

#### Acceptance Criteria

1. THE AWS_IAM_Provider SHALL be added to the ApiTypeEnum in CommonEnums.cs with value 330
2. THE AWS_IAM_Provider SHALL implement the RegexPatterns property with AWS credential patterns
3. THE AWS_IAM_Provider SHALL implement the ValidateKeyWithHttpClientAsync method
4. THE AWS_IAM_Provider SHALL implement the IsValidKeyFormat method to validate Access Key ID format
5. THE Scraper_Service InferProviderFromQuery method SHALL return "AWS IAM" when the query contains "AKIA" or "aws"
6. THE Verifier_Service SHALL automatically discover and use the AWS_IAM_Provider through the [ApiProvider] attribute
7. THE AWS_IAM_Provider SHALL be added to the ProviderRateLimits configuration with a limit of 3 concurrent requests
8. THE AWS_IAM_Provider SHALL follow the existing ValidationResult pattern for returning verification results

### Requirement 8: Handle AWS API Rate Limiting and Errors

**User Story:** As a system operator, I want the system to handle AWS API rate limits gracefully, so that verification continues reliably.

#### Acceptance Criteria

1. WHEN the AWS STS API returns HTTP 429 (rate limit), THE AWS_IAM_Provider SHALL mark the credential as Valid and set ValidationResponse to "Rate limited (key is valid)"
2. WHEN the AWS IAM API returns HTTP 429, THE AWS_IAM_Provider SHALL store "Permission enumeration rate limited" in the metadata
3. WHEN AWS SDK throws a throttling exception, THE AWS_IAM_Provider SHALL apply exponential backoff with jitter
4. WHEN AWS SDK throws a network exception, THE AWS_IAM_Provider SHALL return ValidationResult.HasNetworkError
5. WHEN AWS SDK throws an AccessDenied exception during permission enumeration, THE AWS_IAM_Provider SHALL continue and store partial metadata
6. THE AWS_IAM_Provider SHALL set a 10-second timeout for all AWS API calls
7. THE AWS_IAM_Provider SHALL log all AWS API errors using the ILogger interface
8. WHEN the maximum retry count is exceeded, THE AWS_IAM_Provider SHALL mark the credential status as Error

### Requirement 9: Add AWS Search Queries

**User Story:** As a security researcher, I want the scraper to automatically search for AWS credentials, so that I don't need to manually configure search queries.

#### Acceptance Criteria

1. THE Database_Context SeedDefaultDataAsync method SHALL add "AKIA" to the default search queries
2. THE Database_Context SeedDefaultDataAsync method SHALL add "AWS_ACCESS_KEY_ID" to the default search queries
3. THE Database_Context SeedDefaultDataAsync method SHALL add "AWS_SECRET_ACCESS_KEY" to the default search queries
4. THE Database_Context SeedDefaultDataAsync method SHALL add "aws_access_key_id" to the default search queries
5. THE Database_Context SeedDefaultDataAsync method SHALL add "aws_secret_access_key" to the default search queries
6. THE master_init.sql script SHALL include INSERT statements for AWS search queries
7. THE search queries SHALL be marked as enabled by default

### Requirement 10: Display AWS Metadata in CLI

**User Story:** As a security researcher, I want to see AWS metadata in the CLI interface, so that I can quickly assess discovered credentials.

#### Acceptance Criteria

1. WHEN displaying valid AWS credentials in the CLI, THE Program SHALL show the Account ID
2. WHEN displaying valid AWS credentials in the CLI, THE Program SHALL show the User ARN
3. WHEN displaying valid AWS credentials in the CLI, THE Program SHALL show the credential type
4. WHEN displaying valid AWS credentials in the CLI, THE Program SHALL show the risk level with color coding (red for Critical, yellow for High)
5. WHEN the credential is a root account, THE Program SHALL display a warning message in red
6. WHEN attached policies are available, THE Program SHALL display the policy names
7. THE Program SHALL format AWS metadata in a readable table format using Spectre.Console
8. WHEN AWS metadata is not available, THE Program SHALL display "N/A" for AWS-specific fields
