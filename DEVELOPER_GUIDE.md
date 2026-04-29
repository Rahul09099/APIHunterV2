# Developer Guide - UnsecuredAPIKeys

This guide is for developers who want to extend the capabilities of the UnsecuredAPIKeys tool, specifically by adding support for new AI providers or modifying the Core logic.

## 🏗️ Architecture Overview

The solution follows a clean architecture pattern split into four main projects:

1.  **UnsecuredAPIKeys.CLI**: The entry point. Handles user interaction (Spectre.Console), commands, and startup configuration.
2.  **UnsecuredAPIKeys.Services**: Contains the business logic (`ScraperService`, `VerifierService`, `DatabaseService`).
3.  **UnsecuredAPIKeys.Providers**: Contains the logic for interacting with external APIs (GitHub Search, OpenAI, Anthropic, etc.).
4.  **UnsecuredAPIKeys.Data**: Contains database models (`APIKey`, `RepoReference`) and EF Core Context.

## ➕ How to Add a New AI Provider

To add support for a new AI service (e.g., "NeonAI"), follow these 4 steps:

### Step 1: Add the API Type
Open `UnsecuredAPIKeys.Data\Common\CommonEnums.cs` and add your new provider to the `ApiTypeEnum`:

```csharp
public enum ApiTypeEnum
{
    OpenAI,
    Anthropic,
    GoogleAI,
    // ...
    NeonAI, // <--- Add this
    Other
}
```

### Step 2: Create the Provider Class
Create a new file in `UnsecuredAPIKeys.Providers\AI Providers\NeonAIProvider.cs`.
It must inherit from `BaseApiKeyProvider` and utilize the `[ApiProvider]` attribute.

```csharp
using Microsoft.Extensions.Logging;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Base;
using UnsecuredAPIKeys.Providers.Common; // For ValidationResult

namespace UnsecuredAPIKeys.Providers.AI_Providers
{
    [ApiProvider] // <--- Critical for auto-discovery
    public class NeonAIProvider : BaseApiKeyProvider
    {
        public override string ProviderName => "NeonAI";
        public override ApiTypeEnum ApiType => ApiTypeEnum.NeonAI;

        // Regex patterns to find the key in code
        // Example: neon_sk_12345abcdef
        public override IEnumerable<string> RegexPatterns =>
        [
            @"neon_sk_[a-zA-Z0-9]{32}"
        ];

        public NeonAIProvider() : base() { }
        public NeonAIProvider(ILogger<NeonAIProvider>? logger) : base(logger) { }

        protected override async Task<ValidationResult> ValidateKeyWithHttpClientAsync(
            string apiKey, 
            HttpClient httpClient)
        {
            // Logic to call the API and verify the key
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.neon.ai/v1/user");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var response = await httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                return ValidationResult.Success(response.StatusCode, "Valid NeonAI key");
            }
            
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return ValidationResult.IsUnauthorized(response.StatusCode);
            }

            return ValidationResult.HasHttpError(response.StatusCode, "Unknown error");
        }
    }
}
```

### Step 3: Update Scraper Categorization
To ensure the scraper automatically assigns the correct provider when efficient filtering is needed:

Open `UnsecuredAPIKeys.Services\ScraperService.cs`, find the method `InferProviderFromQuery`, and add:

```csharp
if (q.Contains("neon")) return "NeonAI";
```

### Step 4: Add Default Search Queries
To make the tool search for this provider by default:

Open `UnsecuredAPIKeys.Services\DatabaseService.cs`, find `SeedDefaultDataAsync`, and add:

```csharp
var defaultQueries = new[]
{
    // ...
    "neon_sk_",
    "neon_api_key",
    // ...
};
```

---

## 🛠️ Building from Source

### Prerequisites
- .NET 10.0 SDK

### Build Commands
```powershell
# Restore dependencies
dotnet restore

# Build Debug
dotnet build

# Run
dotnet run --project UnsecuredAPIKeys.CLI
```

## 🧪 Testing Your Provider
1.  Add a known valid (or invalid) key to a text file.
2.  Run the application.
3.  Select `4. Configure Settings` -> `3. Manage Search Queries` and ensure your query is there.
4.  Run a **Lite Search** or **Deep Search**.
5.  If the key is found, go to the Main Menu and select `2. Start Verifier`.
6.  Your `ValidateKeyWithHttpClientAsync` logic will be executed.

---

## ⚡ Performance Architecture

### Pre-Compiled Regex Patterns
All provider regex patterns are compiled once at `ScraperService` startup using `RegexOptions.Compiled`.
This avoids re-compiling the same patterns for every file scanned (~10x faster scraping).

```csharp
// Patterns are compiled in ScraperService constructor:
compiled.Add((provider, new Regex(
    pattern,
    RegexOptions.Compiled | RegexOptions.IgnoreCase,
    TimeSpan.FromSeconds(2)))); // 2s timeout prevents ReDoS
```

### Per-Provider Rate Limiting
`ProviderRateLimiter` (in `VerifierService.cs`) uses a `SemaphoreSlim` per provider to cap concurrent requests.
Limits are configured in `Constants.cs` under `ProviderRateLimits`:

| Provider  | Max Concurrent Requests |
|-----------|------------------------|
| OpenAI    | 5                      |
| Anthropic | 3                      |
| Google    | 5                      |
| DeepSeek  | 3                      |
| Others    | 3                      |

To change a limit, edit `ProviderRateLimits` in `Constants.cs` — no code changes needed.

### Exponential Backoff with Jitter
`BaseApiKeyProvider.ValidateKeyAsync` retries transient network failures with:
- Base delay: `500ms × 2^attempt`
- Jitter: `+0–300ms random` (prevents thundering herd when many keys fail simultaneously)
- Max attempts: 4 (configurable via `GetMaxRetries()` override)

### Session Metrics
`MetricsService.Instance` (singleton) tracks live stats with zero-overhead `Interlocked` counters.
Metrics are visible in the **View Status** screen and flushed to `ApplicationSettings` every 60 seconds.

---

## 📋 Current Provider Model Reference

| Provider    | Validation Endpoint                              | Model Used              |
|-------------|--------------------------------------------------|-------------------------|
| OpenAI      | `/v1/models` + `/v1/chat/completions`            | `gpt-4o-mini` (auto)    |
| Anthropic   | `/v1/messages`                                   | `claude-3-5-haiku-latest` |
| Google AI   | `/v1beta/models` + `generateContent`             | `gemini-1.5-flash` (auto)|
| DeepSeek    | `/v1/chat/completions`                           | `deepseek-chat`         |
| XAI (Grok)  | `/v1/chat/completions`                           | `grok-3-mini`           |
| Together AI | `/v1/chat/completions`                           | `Meta-Llama-3.1-8B-Instruct-Turbo` |
| Cohere      | `/v2/chat`                                       | `command-r`             |
| Fireworks   | `/inference/v1/chat/completions`                 | Auto-discovered         |
| HuggingFace | `/api/whoami`                                    | N/A (account check)     |
| Replicate   | `/v1/account`                                    | N/A (account check)     |
| ElevenLabs  | `/v1/user/subscription`                          | N/A (subscription check)|
| Stability AI| `/v1/user/account`                               | N/A (account check)     |
| RunwayML    | `/v1/organization`                               | N/A (org check)         |
| KlingAI     | `/v1/user/info`                                  | JWT signed              |
| PolloAI     | `/api/platform/credit/balance`                   | N/A (balance check)     |
| A2E AI      | `/api/v1/user/remainingCoins`                    | N/A (balance check)     |
| PiAPI       | `/account/info`                                  | N/A (account check)     |
