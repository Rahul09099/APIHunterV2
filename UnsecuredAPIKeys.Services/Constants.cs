namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Constants for the application.
/// </summary>
public static class LiteLimits
{
    /// <summary>
    /// Maximum valid keys.
    ///
    /// WARNING: If you modify this limit, do NOT publish your database
    /// or results to a public repository. This would expose working API
    /// keys to malicious actors who could abuse them.
    /// </summary>
    public const int MAX_VALID_KEYS = 1000000;

    /// <summary>
    /// Delay between verification batches (milliseconds).
    /// Keep this at least 2000ms on Render free tier to let the thread pool recover.
    /// </summary>
    public const int VERIFICATION_DELAY_MS = 2000;

    /// <summary>
    /// Delay between GitHub search queries (milliseconds).
    /// </summary>
    public const int SEARCH_DELAY_MS = 3000;

    /// <summary>
    /// Number of keys to process per verification batch.
    /// Reduced to 50 to avoid loading too many keys into memory at once on Render free tier.
    /// </summary>
    public const int VERIFICATION_BATCH_SIZE = 50;
}

/// <summary>
/// Application-wide constants.
/// </summary>
public static class AppInfo
{
    public static readonly string Name = "UnsecuredAPIKeys Professional Edition";
    public static readonly string Version = "1.2.0";
    public static readonly string DatabaseName = "unsecuredapikeys.db";
}

/// <summary>
/// Per-provider rate limit configuration.
/// Controls how many concurrent verification requests are sent to each API.
/// Lowering these values reduces the chance of triggering provider-side throttling.
/// </summary>
public static class ProviderRateLimits
{
    /// <summary>Max concurrent requests to OpenAI at once.</summary>
    public const int OpenAI = 5;

    /// <summary>Max concurrent requests to Anthropic at once.</summary>
    public const int Anthropic = 3;

    /// <summary>Max concurrent requests to Google AI at once.</summary>
    public const int Google = 5;

    /// <summary>Max concurrent requests to DeepSeek at once.</summary>
    public const int DeepSeek = 3;

    /// <summary>Max concurrent requests to any other provider at once.</summary>
    public const int Default = 3;

    /// <summary>Max concurrent requests to Groq (very fast, generous limits).</summary>
    public const int Groq = 5;

    /// <summary>Max concurrent requests to Mistral AI.</summary>
    public const int Mistral = 3;

    /// <summary>Max concurrent requests to OpenRouter.</summary>
    public const int OpenRouter = 5;

    /// <summary>Max concurrent requests to Perplexity (each call does a live web search — be conservative).</summary>
    public const int Perplexity = 2;

    /// <summary>Max concurrent requests to Cerebras (very fast inference, generous limits).</summary>
    public const int Cerebras = 5;

    /// <summary>Max concurrent requests to Voyage AI.</summary>
    public const int VoyageAI = 3;

    /// <summary>Max concurrent requests to AWS Bedrock.</summary>
    public const int AWSBedrock = 3;

    /// <summary>Max concurrent requests to Azure OpenAI.</summary>
    public const int AzureOpenAI = 3;

    /// <summary>Max concurrent requests to AWS IAM (conservative to avoid AWS API throttling).</summary>
    public const int AWSIAM = 3;

    /// <summary>
    /// Max concurrent file-content fetches from raw.githubusercontent.com.
    /// GitHub secondary rate limit kicks in around 100 req/min unauthenticated.
    /// </summary>
    public const int GitHubRawContent = 8;

    /// <summary>
    /// Base delay (ms) for exponential backoff on transient failures.
    /// Actual delay = BaseRetryDelayMs * 2^attempt + random jitter.
    /// </summary>
    public const int BaseRetryDelayMs = 500;

    /// <summary>Max jitter added to retry delay (ms) to avoid thundering herd.</summary>
    public const int RetryJitterMs = 300;

    /// <summary>Maximum number of retry attempts for transient network errors.</summary>
    public const int MaxRetryAttempts = 4;
}
