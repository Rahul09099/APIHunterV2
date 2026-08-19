using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Diagnostics;
using System.Text.Json;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers;
using UnsecuredAPIKeys.Providers._Interfaces;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Per-provider rate limiter using SemaphoreSlim.
/// Prevents hammering any single provider with too many concurrent requests.
///
/// IMPACT: Reduces 429 (Too Many Requests) errors from providers.
/// Each provider gets its own semaphore sized from ProviderRateLimits constants.
/// </summary>
internal static class ProviderRateLimiter
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();

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
                "AWS IAM"      => ProviderRateLimits.AWSIAM,
                _              => ProviderRateLimits.Default
            };
            return new SemaphoreSlim(limit, limit);
        });
    }
}

/// <summary>
/// Verifier service that maintains up to 50 valid API keys.
/// When a key becomes invalid, verifies new keys to maintain the limit.
/// </summary>
public class VerifierService(
    DBContext dbContext,
    IDbContextFactory<DBContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    HashSet<ApiTypeEnum>? selectedApiTypes = null,
    bool reVerifyOnly = false,
    ILogger<VerifierService>? logger = null)
{
    private readonly IReadOnlyList<IApiKeyProvider> _providers = ApiProviderRegistry.VerifierProviders;
    private readonly HashSet<ApiTypeEnum>? _selectedApiTypes = selectedApiTypes;
    private CancellationTokenSource? _cancellationTokenSource;

    private int _validCount;
    private int _invalidCount;
    private int _verifiedCount;
    private bool _isIdle;
    private DateTime _serviceStartTime;
    
    public bool IsWorkerMode { get; set; } = string.Equals(Environment.GetEnvironmentVariable("IS_WORKER_MODE"), "true", StringComparison.OrdinalIgnoreCase);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (IsWorkerMode)
        {
            Console.WriteLine("[yellow]⚠️ Verifier skipped: Ghost Worker node is strictly stateless and won't run verification cycles.[/]");
            return;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _serviceStartTime = DateTime.UtcNow;

        Console.WriteLine("[green]Starting verifier service...[/]");
        Console.WriteLine($"[dim]Target valid keys: [yellow]{LiteLimits.MAX_VALID_KEYS}[/][/]");
        Console.WriteLine($"[dim]Loaded {_providers.Count} verification providers[/]");

        if (_selectedApiTypes != null && _selectedApiTypes.Count > 0)
        {
            Console.WriteLine($"[yellow]Verifying only selected API types:[/]");
            foreach (var apiType in _selectedApiTypes.OrderBy(t => t.ToString()))
            {
                Console.WriteLine($"  [dim]- {apiType}[/]");
            }
        }
        else
        {
            foreach (var provider in _providers)
            {
                Console.WriteLine($"  [dim]- {Markup.Escape(provider.ProviderName)}[/]");
            }
        }

        // Run continuously
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                await RunVerificationCycleAsync();

                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                if (_isIdle)
                {
                    Console.WriteLine("[green]Verifier has completed all pending work. Stopping service.[/]");
                    break;
                }

                // Flush metrics to DB periodically (best-effort, non-blocking with isolated DbContext)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var metricsDb = await dbContextFactory.CreateDbContextAsync();
                        await MetricsService.Instance.FlushIfDueAsync(metricsDb);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning("Failed to flush metrics: {Error}", ex.Message);
                    }
                });

                // Wait before next cycle
                if (!_isIdle)
                {
                    Console.WriteLine($"[dim]Waiting {LiteLimits.VERIFICATION_DELAY_MS / 1000}s before next verification cycle...[/]");
                }
                await Task.Delay(LiteLimits.VERIFICATION_DELAY_MS, _cancellationTokenSource.Token);

                // Reset counters
                _validCount = 0;
                _invalidCount = 0;
                _verifiedCount = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[red]Error during verification: {Markup.Escape(ex.Message)}[/]");
                logger?.LogError(ex, "Verification cycle error");
                await Task.Delay(5000, _cancellationTokenSource.Token);
            }
        }

        Console.WriteLine("[green]Verifier stopped.[/]");
    }

    private async Task RunVerificationCycleAsync()
    {
        // Count current valid keys (filtered by selected types if applicable)
        var query = dbContext.APIKeys.Where(k => k.Status == ApiStatusEnum.Valid);
        if (_selectedApiTypes != null && _selectedApiTypes.Count > 0)
        {
            query = query.Where(k => _selectedApiTypes.Contains(k.ApiType));
        }
        var currentValidCount = await query.CountAsync(_cancellationTokenSource!.Token);
        
        if (!_isIdle)
        {
            Console.WriteLine($"[dim]Current valid keys: [yellow]{currentValidCount}[/] / [yellow]{LiteLimits.MAX_VALID_KEYS}[/][/]");
        }

        if (reVerifyOnly || currentValidCount >= LiteLimits.MAX_VALID_KEYS)
        {
            // Re-verify existing valid keys to ensure they're still valid
            await ReVerifyExistingKeysAsync();
        }
        else
        {
            // Verify unverified keys until we reach the limit
            await VerifyNewKeysAsync(LiteLimits.MAX_VALID_KEYS - currentValidCount);
        }

        // Display summary only if not idle
        if (!_isIdle)
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]Metric[/]")
                .AddColumn("[bold]Value[/]");

            table.AddRow("Keys Verified", _verifiedCount.ToString());
            table.AddRow("Now Valid", $"[green]{_validCount}[/]");
            table.AddRow("Now Invalid", $"[red]{_invalidCount}[/]");

            var newValidCount = await dbContext.APIKeys
                .CountAsync(k => k.Status == ApiStatusEnum.Valid, _cancellationTokenSource!.Token);
            table.AddRow("Total Valid", $"[yellow]{newValidCount}[/] / [yellow]{LiteLimits.MAX_VALID_KEYS}[/]");

            AnsiConsole.Write(table);
        }
    }

    private async Task ReVerifyExistingKeysAsync()
    {
        Console.WriteLine("[dim]Re-verifying existing valid keys...[/]");

        // Get oldest verified keys first (filtered by selected types if applicable)
        var query = dbContext.APIKeys.Where(k => (k.Status == ApiStatusEnum.Valid || k.Status == ApiStatusEnum.ValidNoCredits)
            && (k.LastCheckedUTC == null || k.LastCheckedUTC < _serviceStartTime));
            
        if (_selectedApiTypes != null && _selectedApiTypes.Count > 0)
        {
            query = query.Where(k => _selectedApiTypes.Contains(k.ApiType));
        }
        var keysToReVerify = await query
            .OrderBy(k => k.LastCheckedUTC)
            .Take(LiteLimits.VERIFICATION_BATCH_SIZE)
            .ToListAsync(_cancellationTokenSource!.Token);

        if (keysToReVerify.Count == 0)
        {
            if (!_isIdle)
            {
                Console.WriteLine("[yellow]All existing valid keys have been re-verified in this run.[/]");
                _isIdle = true;
            }
            return;
        }

        _isIdle = false;

        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Re-verifying keys[/]", maxValue: keysToReVerify.Count);

                await Parallel.ForEachAsync(keysToReVerify, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 3,
                    CancellationToken = _cancellationTokenSource.Token
                }, async (key, ct) =>
                {
                    try
                    {
                        await using var localDb = await dbContextFactory.CreateDbContextAsync(ct);
                        var localKey = await localDb.APIKeys.FindAsync(new object[] { key.Id }, ct);
                        if (localKey != null)
                        {
                            await VerifyKeyAsync(localDb, localKey);
                            await localDb.SaveChangesAsync(ct);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger?.LogWarning(ex, "Failed to re-verify key {KeyId}", key.Id);
                    }
                    finally
                    {
                        task.Increment(1);
                    }
                });
            });
    }

    private async Task VerifyNewKeysAsync(int neededCount)
    {
        if (!_isIdle)
        {
            Console.WriteLine($"[dim]Verifying unverified keys (need {neededCount} more valid)...[/]");
        }

        // Get unverified keys (filtered by selected types if applicable)
        // Get keys that need verification (Unverified or previously encountered an Error)
        var query = dbContext.APIKeys.Where(k => k.Status == ApiStatusEnum.Unverified || k.Status == ApiStatusEnum.Error);
        if (_selectedApiTypes != null && _selectedApiTypes.Count > 0)
        {
            query = query.Where(k => _selectedApiTypes.Contains(k.ApiType));
        }
        // Debug: Log breakdown of keys for selected types (single query instead of N×5 queries)
        if (_selectedApiTypes != null && _selectedApiTypes.Count > 0 && !_isIdle)
        {
            var breakdown = await dbContext.APIKeys
                .Where(k => _selectedApiTypes.Contains(k.ApiType))
                .GroupBy(k => new { k.ApiType, k.Status })
                .Select(g => new { g.Key.ApiType, g.Key.Status, Count = g.Count() })
                .ToListAsync(_cancellationTokenSource!.Token);

            foreach (var apiType in _selectedApiTypes)
            {
                var unverified = breakdown.FirstOrDefault(x => x.ApiType == apiType && x.Status == ApiStatusEnum.Unverified)?.Count ?? 0;
                var valid      = breakdown.FirstOrDefault(x => x.ApiType == apiType && x.Status == ApiStatusEnum.Valid)?.Count ?? 0;
                var invalid    = breakdown.FirstOrDefault(x => x.ApiType == apiType && x.Status == ApiStatusEnum.Invalid)?.Count ?? 0;
                var noCredits  = breakdown.FirstOrDefault(x => x.ApiType == apiType && x.Status == ApiStatusEnum.ValidNoCredits)?.Count ?? 0;
                var errorCount = breakdown.FirstOrDefault(x => x.ApiType == apiType && x.Status == ApiStatusEnum.Error)?.Count ?? 0;
                Console.WriteLine($"[dim]Type {apiType}: {unverified} unverified, {valid} valid, {invalid} invalid, {noCredits} no-credits, {errorCount} error[/]");
            }
        }

        // Bounded batch fetch: Process at most LiteLimits.VERIFICATION_BATCH_SIZE keys per cycle to prevent OOM
        var batchSize = Math.Min(neededCount * 2, LiteLimits.VERIFICATION_BATCH_SIZE);
        var keysToVerify = await query
            .OrderBy(k => k.FirstFoundUTC)
            .Take(batchSize)
            .ToListAsync(_cancellationTokenSource!.Token);

        if (keysToVerify.Count == 0)
        {
            if (!_isIdle)
            {
                Console.WriteLine("[yellow]No unverified keys available.[/]");
                _isIdle = true;
            }
            return;
        }

        _isIdle = false;

        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Verifying new keys[/]", maxValue: keysToVerify.Count);
                var validFound = 0;

                await Parallel.ForEachAsync(keysToVerify, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 3,
                    CancellationToken = _cancellationTokenSource.Token
                }, async (key, ct) =>
                {
                    if (Volatile.Read(ref validFound) >= neededCount) return;

                    try
                    {
                        // Use a factory-created context per key for concurrency safety
                        await using var localDb = await dbContextFactory.CreateDbContextAsync(ct);
                        var localKey = await localDb.APIKeys.FindAsync(new object[] { key.Id }, ct);
                        if (localKey != null)
                        {
                            var wasValid = await VerifyKeyAsync(localDb, localKey);
                            if (wasValid) Interlocked.Increment(ref validFound);
                            await localDb.SaveChangesAsync(ct);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger?.LogWarning(ex, "Failed to verify key {KeyId}", key.Id);
                    }
                    finally
                    {
                        task.Increment(1);
                    }
                });
            });
    }

    private async Task<bool> VerifyKeyAsync(DBContext localDb, APIKey key)
    {
        Interlocked.Increment(ref _verifiedCount);
        MetricsService.Instance.RecordVerified();

        // Clear previous validation response and reset error count for a fresh start
        key.ValidationResponse = null;
        key.ErrorCount = 0;

        // Build list of providers to try, starting with the assigned one
        var providersToTry = GetProvidersToTry(key);

        if (providersToTry.Count == 0)
        {
            key.Status = ApiStatusEnum.Error;
            key.LastCheckedUTC = DateTime.UtcNow;
            Console.WriteLine($"[yellow]No matching providers for key[/]");
            return false;
        }

        // Try each matching provider until one succeeds
        foreach (var provider in providersToTry)
        {
            // Acquire per-provider rate limit slot before making the API call
            var semaphore = ProviderRateLimiter.GetSemaphore(provider.ProviderName);
            await semaphore.WaitAsync(_cancellationTokenSource!.Token);

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await provider.ValidateKeyAsync(key.ApiKey, httpClientFactory);
                sw.Stop();

                // Record latency for this provider
                MetricsService.Instance.RecordProviderLatency(provider.ProviderName, sw.ElapsedMilliseconds);

                key.ValidationResponse = result.Detail;
                key.LastCheckedUTC = DateTime.UtcNow;

                switch (result.Status)
                {
                    case Providers.Common.ValidationAttemptStatus.Valid:
                        // Check if the response indicates quota/rate limit issues FIRST
                        // Priority 1: Explicit flag from provider
                        // Priority 2: Keyword search (fallback for older providers)
                        if (result.IsQuotaExceeded ||
                            result.Detail?.Contains("quota", StringComparison.OrdinalIgnoreCase) == true ||
                            result.Detail?.Contains("credit exhausted", StringComparison.OrdinalIgnoreCase) == true ||
                            result.Detail?.Contains("insufficient quota", StringComparison.OrdinalIgnoreCase) == true ||
                            result.Detail?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true ||
                            result.Detail?.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            // Update the key's API type if a different provider validated it
                            if (key.ApiType != provider.ApiType)
                            {
                                Console.WriteLine($"[dim]Reclassified from {key.ApiType} to {provider.ApiType}[/]");
                                key.ApiType = provider.ApiType;
                            }
                            key.Status = ApiStatusEnum.ValidNoCredits;
                            key.ErrorCount = 0;
                            key.ValidationResponse = result.RawResponse ?? result.Detail ?? "Valid key but no credits";
                            Interlocked.Increment(ref _validCount);
                            Console.WriteLine($"[yellow]✓ Valid [no credits]: {Markup.Escape(provider.ProviderName)} key[/]");
                            if (!string.IsNullOrEmpty(result.Detail))
                                Console.WriteLine($"  [dim]Response: {Markup.Escape(result.Detail.Length > 100 ? result.Detail.Substring(0, 100) + "..." : result.Detail)}[/]");
                            return true;
                        }
                        
                        // Otherwise, it's genuinely valid with credits
                        // Update the key's API type if a different provider validated it
                        if (key.ApiType != provider.ApiType)
                        {
                            Console.WriteLine($"[dim]Reclassified from {key.ApiType} to {provider.ApiType}[/]");
                            key.ApiType = provider.ApiType;
                        }
                        // Update balance and account tier info
                        key.Balance = result.Balance;
                        key.AccountTier = result.AccountTier;
                        key.ValidationResponse = result.RawResponse ?? result.Detail ?? "Key is valid";
                        
                        // Merge models and structured metadata into Metadata field
                        var metadataObj = new Dictionary<string, object>();
                        if (result.Metadata != null)
                        {
                            foreach (var kvp in result.Metadata)
                                metadataObj[kvp.Key] = kvp.Value;
                        }

                        if (result.AvailableModels != null && result.AvailableModels.Any())
                        {
                            metadataObj["available_models"] = result.AvailableModels;
                        }

                        // Special case: Remove metadata for providers where standard fields are sufficient to save space
                        if (provider.ApiType == ApiTypeEnum.DeepSeek || provider.ApiType == ApiTypeEnum.OpenRouter)
                        {
                            key.Metadata = null;
                        }
                        else if (metadataObj.Any())
                        {
                            key.Metadata = JsonSerializer.Serialize(metadataObj, new JsonSerializerOptions { WriteIndented = true });
                        }
                        else
                        {
                            key.Metadata = null;
                        }

                        // Capture AWS metadata if applicable
                        if (result.AwsAccountId != null || result.AwsUserArn != null)
                        {
                            key.AwsAccountId = result.AwsAccountId;
                            key.AwsUserArn = result.AwsUserArn;
                            key.AwsUserId = result.AwsUserId;
                            key.AwsCredentialType = result.AwsCredentialType;
                            key.AwsRiskLevel = result.AwsRiskLevel;
                            key.AwsIsRootAccount = result.AwsIsRootAccount;
                            key.AwsAttachedPolicies = result.AwsAttachedPolicies != null
                                ? System.Text.Json.JsonSerializer.Serialize(result.AwsAttachedPolicies)
                                : null;
                        }

                        key.Status = ApiStatusEnum.Valid;
                        key.ErrorCount = 0;
                        key.ValidationResponse = result.RawResponse ?? result.Detail ?? "Key is valid";
                        Interlocked.Increment(ref _validCount);
                        MetricsService.Instance.RecordValid();
                        
                        var balanceInfo = !string.IsNullOrEmpty(key.Balance) ? $" [dim](Balance: {key.Balance})[/]" : "";
                        var tierInfo = !string.IsNullOrEmpty(key.AccountTier) ? $" [dim](Tier: {key.AccountTier})[/]" : "";
                        
                        Console.WriteLine($"[green]✓ Valid: {Markup.Escape(provider.ProviderName)} key[/]{balanceInfo}{tierInfo}");
                        if (!string.IsNullOrEmpty(result.Detail) && result.Detail != "Key is valid")
                            Console.WriteLine($"  [dim]Response: {Markup.Escape(result.Detail.Length > 100 ? result.Detail.Substring(0, 100) + "..." : result.Detail)}[/]");
                        return true;

                    case Providers.Common.ValidationAttemptStatus.HttpError:
                        // Check if it's a quota/credits issue based on detail
                        if (result.IsQuotaExceeded ||
                            result.Detail?.Contains("quota", StringComparison.OrdinalIgnoreCase) == true ||
                            result.Detail?.Contains("credit exhausted", StringComparison.OrdinalIgnoreCase) == true ||
                            result.Detail?.Contains("insufficient quota", StringComparison.OrdinalIgnoreCase) == true ||
                            result.Detail?.Contains("billing", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            // Update the key's API type if a different provider validated it
                            if (key.ApiType != provider.ApiType)
                            {
                                Console.WriteLine($"[dim]Reclassified from {key.ApiType} to {provider.ApiType}[/]");
                                key.ApiType = provider.ApiType;
                            }
                            key.Status = ApiStatusEnum.ValidNoCredits;
                            key.ErrorCount = 0;
                            key.ValidationResponse = result.Detail ?? "Valid key but quota issue";
                            Interlocked.Increment(ref _validCount);
                            Console.WriteLine($"[yellow]✓ Valid [no credits]: {Markup.Escape(provider.ProviderName)} key[/]");
                            if (!string.IsNullOrEmpty(result.Detail))
                                Console.WriteLine($"  [dim]Response: {Markup.Escape(result.Detail.Length > 100 ? result.Detail.Substring(0, 100) + "..." : result.Detail)}[/]");
                            return true;
                        }
                        // HTTP error but not quota - try next provider
                        continue;

                    case Providers.Common.ValidationAttemptStatus.Unauthorized:
                        // If the provider explicitly says "leaked", trust it and stop trying others.
                        if (result.Detail?.Contains("leaked", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            key.Status = ApiStatusEnum.Invalid;
                            key.ValidationResponse = result.Detail;
                            // Reset error count as this is a definitive result, not a transient error
                            key.ErrorCount = 0; 
                            
                            // Optimization: Purge references for invalid keys to save space
                            await PurgeKeyReferencesAsync(localDb, key);
                            
                            return false; // Stop checking other providers
                        }

                        // This provider explicitly rejected it - try next provider
                        continue;

                    case Providers.Common.ValidationAttemptStatus.NetworkError:
                        // Network error - don't try other providers, just increment error count
                        MetricsService.Instance.RecordNetworkError();
                        key.ErrorCount++;
                        if (key.ErrorCount >= 3)
                        {
                            key.Status = ApiStatusEnum.Error;
                            key.ValidationResponse = $"Network error: {result.Detail}";
                        }
                        return false;

                    default:
                        // Provider-specific error - try next provider
                        continue;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Error verifying key {KeyId} with provider {Provider}", key.Id, provider.ProviderName);
                key.ValidationResponse = $"Exception: {ex.Message}";
                // Continue to next provider on exception
                continue;
            }
            finally
            {
                // Always release the rate limit slot, whether we returned, continued, or threw
                semaphore.Release();
            }
        }

        // All providers failed - mark as invalid
        key.Status = ApiStatusEnum.Invalid;
        if (string.IsNullOrEmpty(key.ValidationResponse))
        {
             key.ValidationResponse = "Invalid or unauthorized";
        }
        
        Interlocked.Increment(ref _invalidCount);
        MetricsService.Instance.RecordInvalid();
        
        // Optimization: Purge references for invalid keys to save space
        await PurgeKeyReferencesAsync(localDb, key);
        
        return false;
    }

    private async Task PurgeKeyReferencesAsync(DBContext localDb, APIKey key)
    {
        try
        {
            // 1. Delete associated repo references to reclaim 90%+ disk space
            await localDb.RepoReferences
                .Where(r => r.APIKeyId == key.Id)
                .ExecuteDeleteAsync();

            // 2. Strip large text fields to leave only a lightweight ~60 byte tombstone
            key.ValidationResponse = null;
            key.Metadata = null;
            key.Balance = null;
            key.AccountTier = null;
            key.AwsAttachedPolicies = null;
            key.Status = ApiStatusEnum.Invalid;
            key.LastCheckedUTC = DateTime.UtcNow;

            // Keep the key row in APIKeys table so Scraper deduplication skips it!
            Console.WriteLine($"[dim][DB] Purged repo references for invalid APIKey #{key.Id} (retained for deduplication)[/]");
        }
        catch (Exception ex)
        {
            // Fail silently — reference cleanup is an optimization, not critical
            logger?.LogWarning(ex, "Failed to purge key references for {KeyId}", key.Id);
        }
    }

    /// <summary>
    /// Gets providers to try for a key, ordered by: assigned provider first, then other matching providers.
    /// </summary>
    private List<IApiKeyProvider> GetProvidersToTry(APIKey key)
    {
        var result = new List<IApiKeyProvider>();

        // First, add the assigned provider (if it exists)
        var assignedProvider = _providers.FirstOrDefault(p => p.ApiType == key.ApiType);
        if (assignedProvider != null)
        {
            // Always try the assigned provider, even if we are filtering the verification run.
            // If the key is in our queue, we should verify it properly.
            result.Add(assignedProvider);
        }

        // Then add other providers whose patterns match this key
        foreach (var provider in _providers)
        {
            // Skip the already-added assigned provider
            if (provider.ApiType == key.ApiType)
                continue;

            // Note: We intentionally do NOT filter by _selectedApiTypes here.
            // If the user selected "DeepSeek" but we found a key that matches "OpenAI" patterns,
            // we should try validating it as OpenAI as a fallback if DeepSeek fails.
            // The _selectedApiTypes filter is used to select WHICH keys to run, not HOW to run them.

            // Check if any of this provider's patterns match the key
            foreach (var pattern in provider.RegexPatterns)
            {
                try
                {
                    var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2));
                    if (regex.IsMatch(key.ApiKey))
                    {
                        result.Add(provider);
                        break; // One match is enough for this provider
                    }
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException ex)
                {
                    logger?.LogWarning(ex, "Regex matching timed out for provider {Provider}", provider.ProviderName);
                }
                catch (ArgumentException ex)
                {
                    logger?.LogWarning(ex, "Invalid regex pattern for provider {Provider}", provider.ProviderName);
                }
            }
        }

        return result;
    }
}
