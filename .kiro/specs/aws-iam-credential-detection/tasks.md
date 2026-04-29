# Implementation Plan: AWS IAM Credential Detection and Verification

## Overview

This implementation plan breaks down the AWS IAM credential detection feature into discrete, executable tasks. The feature adds comprehensive AWS IAM Access Key detection, verification, and metadata extraction to the UnsecuredAPIKeys tool. Each task builds incrementally on previous work, with checkpoints to ensure stability.

The implementation follows the existing provider architecture pattern and integrates seamlessly with the current codebase. All AWS-specific metadata will be stored in the database and included in export functionality.

## Tasks

- [x] 1. Add AWS IAM to core enums and configure project dependencies
  - Add `AWSIAM = 330` to `ApiTypeEnum` in `UnsecuredAPIKeys.Data/Common/CommonEnums.cs`
  - Add AWS SDK NuGet packages to `UnsecuredAPIKeys.Providers/UnsecuredAPIKeys.Providers.csproj`:
    - `AWSSDK.SecurityToken` version 3.7.400
    - `AWSSDK.IdentityManagement` version 3.7.400
    - `AWSSDK.Core` version 3.7.400
  - _Requirements: 7.1, 7.2_

- [x] 2. Update database schema with AWS metadata columns
  - [x] 2.1 Add AWS properties to APIKey model
    - Add 7 new properties to `UnsecuredAPIKeys.Data/Models/APIKey.cs`:
      - `AwsAccountId` (string, nullable)
      - `AwsUserArn` (string, nullable)
      - `AwsUserId` (string, nullable)
      - `AwsCredentialType` (string, nullable)
      - `AwsAttachedPolicies` (string, nullable, JSON serialized)
      - `AwsRiskLevel` (string, nullable)
      - `AwsIsRootAccount` (bool, default false)
    - _Requirements: 5.1, 5.2, 5.3, 5.4, 5.5, 5.6, 5.7_

  - [x] 2.2 Add AWS properties to ValidationResult
    - Add matching AWS properties to `UnsecuredAPIKeys.Providers/Common/ValidationResult.cs`
    - Include all 7 AWS metadata fields
    - _Requirements: 5.8_

  - [x] 2.3 Update database migration script
    - Add AWS column definitions to `master_init.sql` with idempotent ALTER TABLE statements
    - Add indexes for `AwsAccountId` and `AwsRiskLevel`
    - _Requirements: 5.9_

- [x] 3. Implement AWSIAMProvider class structure and credential detection
  - [x] 3.1 Create AWSIAMProvider class file
    - Create `UnsecuredAPIKeys.Providers/Cloud Providers/AWSIAMProvider.cs`
    - Inherit from `BaseApiKeyProvider`
    - Add `[ApiProvider]` attribute for auto-discovery
    - Set `ProviderName` to "AWS IAM"
    - Set `ApiType` to `ApiTypeEnum.AWSIAM`
    - _Requirements: 1.6, 7.2, 7.3, 7.7_

  - [x] 3.2 Implement regex patterns for AWS credential detection
    - Add `RegexPatterns` property with 8 patterns:
      - Access Key ID pattern: `\bAKIA[0-9A-Z]{16}\b`
      - Session Token pattern: `\bASIA[0-9A-Z]{16}\b`
      - Environment variable patterns for access key and secret
      - Combined patterns in code
    - _Requirements: 1.1, 1.2, 1.5_

  - [x] 3.3 Implement credential pair extraction logic
    - Create `ExtractCredentialPair` method to parse Access Key ID and Secret Access Key
    - Handle delimited format (AKIA:::secret)
    - Handle standalone Access Key ID (secret found in context)
    - Implement `IsValidKeyFormat` to validate Access Key ID format
    - _Requirements: 1.3, 1.4, 7.4_

- [x] 4. Implement AWS SDK client configuration
  - [x] 4.1 Create STS client factory method
    - Implement `CreateStsClient` method with region parameter
    - Configure BasicAWSCredentials with access key and secret
    - Set timeout to 10 seconds and max retry to 2
    - Support us-east-1 and us-west-2 regions
    - _Requirements: 2.6, 2.7_

  - [x] 4.2 Create IAM client factory method
    - Implement `CreateIamClient` method
    - Configure BasicAWSCredentials
    - Set timeout to 10 seconds and max retry to 2
    - Use us-east-1 region (IAM is global)
    - _Requirements: 4.1_

- [x] 5. Implement core validation logic
  - [x] 5.1 Implement ValidateKeyWithHttpClientAsync method
    - Extract credential pair using `ExtractCredentialPair`
    - Return error if Secret Access Key not found
    - Call `VerifyCredentialsAsync` to validate with STS
    - Call `ExtractMetadataAsync` to parse STS response
    - Call `EnumeratePermissionsAsync` (best effort)
    - Call `CalculateRiskLevel` based on policies
    - Populate ValidationResult with all AWS metadata
    - Handle all AWS exception types
    - _Requirements: 2.1, 2.2, 2.3, 2.4, 2.5, 8.1, 8.2, 8.3, 8.4, 8.5, 8.7_

  - [x] 5.2 Implement STS credential verification
    - Create `VerifyCredentialsAsync` method
    - Call AWS STS GetCallerIdentity API
    - Try primary region (us-east-1) first
    - Implement fallback to us-west-2 for non-auth errors
    - Handle InvalidClientTokenId and SignatureDoesNotMatch errors
    - _Requirements: 2.1, 2.2, 2.3, 2.8_

- [x] 6. Checkpoint - Ensure basic validation works
  - Ensure all tests pass, ask the user if questions arise.

- [x] 7. Implement AWS metadata extraction
  - [x] 7.1 Create AwsMetadata internal class
    - Define properties: AccountId, UserArn, UserId, UserName, CredentialType, IsRootAccount, AttachedPolicies
    - _Requirements: 3.1, 3.2, 3.3_

  - [x] 7.2 Implement ExtractMetadataAsync method
    - Parse Account ID from GetCallerIdentity response
    - Parse User ARN from response
    - Parse User ID from response
    - Detect root account (ARN contains ":root")
    - Extract IAM username from ARN (":user/")
    - Extract role name from ARN (":assumed-role/")
    - Set credential type based on ARN format
    - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7, 3.8, 3.9_

- [x] 8. Implement IAM permission enumeration
  - [x] 8.1 Implement EnumeratePermissionsAsync method
    - Return empty list for root, unknown, or empty usernames
    - Call IAM ListAttachedUserPolicies API
    - Extract policy names from response
    - Handle AccessDenied errors gracefully (store "Permission enumeration denied")
    - Log warnings for enumeration failures
    - _Requirements: 4.1, 4.2, 4.3, 4.6, 8.5_

  - [x] 8.2 Implement CalculateRiskLevel method
    - Return "Critical" for root accounts
    - Return "Critical" for AdministratorAccess policy
    - Return "High" for PowerUserAccess or *FullAccess patterns
    - Return "Medium" for Write/Modify/Delete/Create/Put patterns
    - Return "Low" for read-only or limited permissions
    - _Requirements: 4.4, 4.5, 4.8_

- [x] 9. Update export service for AWS metadata
  - [x] 9.1 Update CSV export
    - Modify `ExportAsCsvAsync` in export service
    - Add AWS columns to CSV header: AwsAccountId, AwsUserArn, AwsCredentialType, AwsRiskLevel, AwsIsRootAccount, AwsAttachedPolicies
    - Format AwsAttachedPolicies JSON array as semicolon-separated string
    - Handle null AWS metadata with empty strings
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6, 6.8, 6.9_

  - [x] 9.2 Update JSON export
    - Modify `ExportAsJsonAsync` in export service
    - Add AwsMetadata nested object with all 7 fields
    - Deserialize AwsAttachedPolicies from JSON string to array
    - Handle null AWS metadata (exclude from JSON)
    - _Requirements: 6.7, 6.8_

- [x] 10. Update CLI display for AWS metadata
  - [x] 10.1 Update key details display
    - Modify `DisplayKeyDetails` in `UnsecuredAPIKeys.CLI/Program.cs`
    - Add AWS Account ID row with cyan color
    - Add AWS User ARN row with dim color
    - Add AWS Credential Type row with yellow color
    - Add AWS Risk Level row with color coding (red=Critical, orange=High, yellow=Medium, green=Low)
    - Add ROOT ACCOUNT warning row in red for root accounts
    - Add AWS Attached Policies row with bullet list formatting
    - _Requirements: 10.1, 10.2, 10.3, 10.4, 10.5, 10.6, 10.7_

  - [x] 10.2 Update key list display
    - Modify `DisplayValidKeys` in `UnsecuredAPIKeys.CLI/Program.cs`
    - Add "AWS Account" column showing AwsAccountId or "N/A"
    - Add "Risk" column with color-coded risk level
    - _Requirements: 10.8_

- [x] 11. Add AWS search queries
  - [x] 11.1 Update search query seeding
    - Add AWS queries to `SeedDefaultDataAsync` in database service:
      - "AKIA"
      - "ASIA"
      - "AWS_ACCESS_KEY_ID"
      - "AWS_SECRET_ACCESS_KEY"
      - "aws_access_key_id"
      - "aws_secret_access_key"
    - Mark all queries as enabled by default
    - _Requirements: 9.1, 9.2, 9.3, 9.4, 9.5, 9.7_

  - [x] 11.2 Update SQL migration script
    - Add AWS query INSERT statements to `master_init.sql`
    - Use idempotent INSERT with NOT EXISTS check
    - _Requirements: 9.6_

  - [x] 11.3 Update scraper provider inference
    - Modify `InferProviderFromQuery` in scraper service
    - Add AWS IAM detection for queries containing "akia", "asia", or "aws"
    - _Requirements: 7.5_

- [x] 12. Configure rate limiting for AWS IAM
  - Add `AWSIAM = 3` constant to `ProviderRateLimits` class
  - Update `ProviderRateLimiter.GetSemaphore` switch statement with "AWS IAM" case
  - _Requirements: 7.7, 8.6_

- [x] 13. Final checkpoint - Integration testing
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- This feature integrates with the existing provider architecture and requires no changes to the core verification loop
- AWS SDK clients are created per-validation to avoid credential caching issues
- Permission enumeration is best-effort and will not block validation if it fails
- Rate limiting is set to 3 concurrent requests to avoid AWS API throttling
- All AWS metadata is optional and will gracefully handle null values in exports and display
- The implementation follows the same patterns as existing providers (OpenAI, Anthropic, etc.)
- Database schema changes are idempotent and safe to run multiple times
