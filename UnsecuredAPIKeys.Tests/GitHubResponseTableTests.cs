using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 10.2 — Complete GitHub Search and Content Response Table Suite.
/// Validates AC-6.1–AC-6.34, AC-16.4–AC-16.15, AC-17.37, AC-17.39, AC-17.8, AC-16.26.
/// </summary>
public sealed class GitHubResponseTableTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class FixedClock(DateTime utcNow) : IDatabaseUtcClock
    {
        public Task<DateTime> GetUtcNowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(utcNow);
    }

    private sealed class ZeroJitterSource : ISchedulerJitterSource
    {
        public TimeSpan Next(TimeSpan maxJitter) => TimeSpan.Zero;
    }

    private static (DBContext Db, SqliteCredentialScheduler Scheduler, SearchProviderToken Token) SetupScheduler(
        DateTime now,
        long maxConcurrent = 5)
    {
        var options = new DbContextOptionsBuilder<DBContext>()
            .UseSqlite($"Data Source=file:test_gh_{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        var db = new DBContext(options);
        db.Database.EnsureCreated();

        var instance = db.SearchProviderInstances
            .FirstOrDefault(i => i.StableId == ProviderInstanceSchema.DefaultGitHubStableId);
        if (instance is null)
        {
            instance = new SearchProviderInstance
            {
                StableId = ProviderInstanceSchema.DefaultGitHubStableId,
                ProviderKind = SearchProviderEnum.GitHub,
                DisplayName = "GitHub SaaS",
                NormalizedScheme = "https",
                NormalizedHost = "api.github.com",
                NormalizedPort = 443,
                NormalizedBasePath = "/",
                IsEnabled = true,
                MaxConcurrentOperations = (int)maxConcurrent,
                SettingsVersion = 1,
                SettingsJson = "{}",
                CreatedUtc = now,
                UpdatedUtc = now
            };
            db.SearchProviderInstances.Add(instance);
            db.SaveChanges();
        }

        var token = new SearchProviderToken
        {
            StableId = Guid.NewGuid(),
            ProviderInstanceId = instance.Id,
            SearchProvider = SearchProviderEnum.GitHub,
            IsEnabled = true,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        db.SearchProviderTokens.Add(token);

        var workItem = new WorkItem
        {
            StableId = Guid.NewGuid(),
            ProviderInstanceId = instance.Id,
            ProviderKind = SearchProviderEnum.GitHub,
            EffectiveQueryHash = new string('a', 64),
            AdapterVersion = "github-v1",
            CreatedUtc = now,
            UpdatedUtc = now
        };
        db.WorkItems.Add(workItem);
        db.SaveChanges();

        var policy = new LeasePolicyOptions();
        var clock = new FixedClock(now);
        var scheduler = new SqliteCredentialScheduler(
            db, policy, new ZeroJitterSource(), clock, NullLogger<SqliteCredentialScheduler>.Instance);

        return (db, scheduler, token);
    }

    // ── 1. HTTP 401: AuthInvalid (AC-16.9) ────────────────────────────────────────

    [Fact]
    public async Task Response401_ClassifiesAsAuthInvalid_AndSchedulerDisablesCredential()
    {
        var classifier = new GitHubResponseClassifier();
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"message\":\"Bad credentials\",\"documentation_url\":\"https://docs.github.com/rest\"}", Encoding.UTF8, "application/json")
        };

        var classification = classifier.ClassifyResponse(response, await response.Content.ReadAsStringAsync());
        Assert.Equal(ProviderOutcomeKind.AuthInvalid, classification.Outcome);

        // Verify scheduler transition
        var now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        var (db, scheduler, token) = SetupScheduler(now);

        var workItem = await db.WorkItems.FirstAsync();
        var instance = await db.SearchProviderInstances.FirstAsync();

        var claim = await scheduler.TryClaimAsync(
            instance.StableId, workItem.StableId, "part-1", "node-1", Guid.NewGuid(),
            CredentialGrantScope.Global, null);
        Assert.True(claim.IsSuccess);

        var completion = await scheduler.CompleteAsync(
            claim.Success!.LeaseId,
            token.StableId,
            claim.Success.CredentialRevision,
            ProviderOutcomeKind.AuthInvalid);
        Assert.True(completion.Succeeded);

        var updated = await db.SearchProviderTokens.SingleAsync(t => t.StableId == token.StableId);
        Assert.False(updated.IsEnabled, "AuthInvalid MUST disable the credential.");
        Assert.Equal("AuthInvalid", updated.DisabledReason);
        Assert.NotNull(updated.DisabledAtUtc);
    }

    // ── 2. HTTP 403: Ordinary Permission / ForbiddenScope (AC-16.10) ──────────────

    [Fact]
    public async Task Response403_WithoutRateLimitHeaders_ClassifiesAsForbiddenScope_AndAppliesCooldownWithoutDisabling()
    {
        var classifier = new GitHubResponseClassifier();
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"message\":\"Resource not accessible by personal access token\"}", Encoding.UTF8, "application/json")
        };

        var classification = classifier.ClassifyResponse(response, await response.Content.ReadAsStringAsync());
        Assert.Equal(ProviderOutcomeKind.ForbiddenScope, classification.Outcome);

        // Verify scheduler transition: applies policy cooldown without disabling
        var now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        var (db, scheduler, token) = SetupScheduler(now);

        var workItem = await db.WorkItems.FirstAsync();
        var instance = await db.SearchProviderInstances.FirstAsync();

        var claim = await scheduler.TryClaimAsync(
            instance.StableId, workItem.StableId, "part-1", "node-1", Guid.NewGuid(),
            CredentialGrantScope.Global, null);
        Assert.True(claim.IsSuccess);

        var completion = await scheduler.CompleteAsync(
            claim.Success!.LeaseId,
            token.StableId,
            claim.Success.CredentialRevision,
            ProviderOutcomeKind.ForbiddenScope);
        Assert.True(completion.Succeeded);

        var updated = await db.SearchProviderTokens.SingleAsync(t => t.StableId == token.StableId);
        Assert.True(updated.IsEnabled, "ForbiddenScope SHALL NOT disable the credential (AC-6.16).");
        Assert.NotNull(updated.CooldownUntilUtc);
        Assert.True(updated.CooldownUntilUtc > now);
    }

    // ── 3. HTTP 403: Primary Rate Limit (AC-16.6) ──────────────────────────────────

    [Fact]
    public async Task Response403_WithZeroRemaining_ClassifiesAsRateLimitedWithResetTime()
    {
        var classifier = new GitHubResponseClassifier();
        var resetEpoch = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds();
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"message\":\"API rate limit exceeded for user\"}", Encoding.UTF8, "application/json")
        };
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", resetEpoch.ToString());

        var classification = classifier.ClassifyResponse(response, await response.Content.ReadAsStringAsync());
        Assert.Equal(ProviderOutcomeKind.RateLimited, classification.Outcome);
        Assert.NotNull(classification.RetryAfter);
        Assert.True(classification.RetryAfter.Value > TimeSpan.Zero);
    }

    // ── 4. Secondary Rate Limit Allowlist (AC-16.7) ────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "You have exceeded a secondary rate limit. Please wait a few minutes.")]
    [InlineData(HttpStatusCode.Forbidden, "Please wait a few minutes before you try again.")]
    [InlineData(HttpStatusCode.TooManyRequests, "You have triggered an abuse detection mechanism.")]
    public async Task SecondaryRateLimit_AllowlistedMessage_ClassifiesAsRateLimitedWithDelay(HttpStatusCode code, string message)
    {
        var classifier = new GitHubResponseClassifier();
        var response = new HttpResponseMessage(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json")
        };
        response.Headers.Add("Retry-After", "120");

        var classification = classifier.ClassifyResponse(response, await response.Content.ReadAsStringAsync());
        Assert.Equal(ProviderOutcomeKind.RateLimited, classification.Outcome);
        Assert.NotNull(classification.RetryAfter);
        Assert.True(classification.RetryAfter.Value >= TimeSpan.FromSeconds(60));
    }

    // ── 5. HTTP 429: Bare Rate Limit (AC-16.8) ────────────────────────────────────

    [Fact]
    public void Response429_BareWithoutHeaders_ClassifiesAsRateLimitedWithDefaultFallback()
    {
        var classifier = new GitHubResponseClassifier();
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        var classification = classifier.ClassifyResponse(response);
        Assert.Equal(ProviderOutcomeKind.RateLimited, classification.Outcome);
        Assert.NotNull(classification.RetryAfter);
    }

    // ── 6. HTTP 400 & 422: Invalid Request (AC-16.12) ─────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"message\":\"Problems parsing query\"}")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"message\":\"Validation Failed\",\"errors\":[{\"message\":\"The listed users and repositories cannot be searched\"}]}")]
    public async Task Response400Or422_ClassifiesAsRequestInvalid(HttpStatusCode code, string body)
    {
        var classifier = new GitHubResponseClassifier();
        var response = new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        var classification = classifier.ClassifyResponse(response, await response.Content.ReadAsStringAsync());
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, classification.Outcome);
    }

    // ── 7. HTTP 404: Contextual Resource Missing (AC-16.11) ───────────────────────

    [Fact]
    public async Task Response404_ClassifiesAsResourceMissing_NeverAuthInvalid()
    {
        var classifier = new GitHubResponseClassifier();
        var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"message\":\"Not Found\"}", Encoding.UTF8, "application/json")
        };

        var classification = classifier.ClassifyResponse(response, await response.Content.ReadAsStringAsync());
        Assert.Equal(ProviderOutcomeKind.ResourceMissing, classification.Outcome);
        Assert.NotEqual(ProviderOutcomeKind.AuthInvalid, classification.Outcome);
    }

    // ── 8. HTTP 408 / 5xx / Connectivity: Transient (AC-16.13) ─────────────────────

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void Response5xxAndTimeout_ClassifiesAsTransient(HttpStatusCode code)
    {
        var classifier = new GitHubResponseClassifier();
        var response = new HttpResponseMessage(code);

        var classification = classifier.ClassifyResponse(response);
        Assert.Equal(ProviderOutcomeKind.Transient, classification.Outcome);
    }

    [Fact]
    public void HttpRequestException_ClassifiesAsTransient()
    {
        var classifier = new GitHubResponseClassifier();
        var classification = classifier.Classify(
            SearchProviderEnum.GitHub,
            null,
            new HttpRequestException("Connection reset by peer"));

        Assert.Equal(ProviderOutcomeKind.Transient, classification);
    }

    [Fact]
    public void OperationCanceledException_ClassifiesAsCancellation()
    {
        var classifier = new GitHubResponseClassifier();
        var classification = classifier.Classify(
            SearchProviderEnum.GitHub,
            null,
            new OperationCanceledException());

        Assert.Equal(ProviderOutcomeKind.Cancellation, classification);
    }

    // ── 9. Redaction Boundary: Raw Bodies and Secrets (AC-16.26, AC-17.8) ─────────

    [Fact]
    public async Task ErrorClassifier_NeverRetainsOrDisclosesRawErrorBody()
    {
        var classifier = new GitHubResponseClassifier();
        const string secretCanary = "ghp_CANARY_SECRET_NEVER_LOG_987654321";
        var rawBody = $"{{\"message\":\"Bad credentials\",\"secret_leak\":\"{secretCanary}\"}}";

        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json")
        };

        var classification = classifier.ClassifyResponse(response, rawBody);
        Assert.Equal(ProviderOutcomeKind.AuthInvalid, classification.Outcome);

        // Ensure safe error representation does not contain secretCanary
        var sanitizedCode = classification.SanitizedCode;
        if (!string.IsNullOrEmpty(sanitizedCode))
        {
            Assert.DoesNotContain(secretCanary, sanitizedCode, StringComparison.Ordinal);
        }
    }
}
