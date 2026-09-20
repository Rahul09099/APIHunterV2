using System.Net;
using System.Text;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Providers._Interfaces;
using UnsecuredAPIKeys.Providers.Search_Providers;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 11.1 (partial) / 11.2 — GitLab search/content response classification table.
/// Validates AC-6.1–AC-6.34, AC-16.16–AC-16.26: Typed Outcome per status, Scheduler-relevant
/// Retry-After/reset evidence, raw-body redaction (bounded allowlisted evidence only).
/// </summary>
public sealed class GitLabResponseClassifierTests
{
    private static HttpResponseMessage Response(
        HttpStatusCode status,
        string? body = null,
        params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

    [Fact]
    public void Success200_ClassifiesAsSuccess()
    {
        var classifier = new GitLabResponseClassifier();
        using var response = Response(HttpStatusCode.OK, "[]");
        var result = classifier.ClassifyResponse(response, "[]");
        Assert.Equal(ProviderOutcomeKind.Success, result.Outcome);
    }

    [Fact]
    public void Unauthorized401_ClassifiesAsAuthInvalid()
    {
        var classifier = new GitLabResponseClassifier();
        using var response = Response(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""");
        var result = classifier.ClassifyResponse(response, """{"message":"401 Unauthorized"}""");
        Assert.Equal(ProviderOutcomeKind.AuthInvalid, result.Outcome);
        Assert.Equal("BadCredentials", result.SanitizedCode);
    }

    [Fact]
    public void Forbidden403_OrdinaryPermission_ClassifiesAsForbiddenScope()
    {
        var classifier = new GitLabResponseClassifier();
        using var response = Response(HttpStatusCode.Forbidden, """{"message":"403 Forbidden"}""");
        var result = classifier.ClassifyResponse(response, """{"message":"403 Forbidden"}""");
        Assert.Equal(ProviderOutcomeKind.ForbiddenScope, result.Outcome);
    }

    [Fact]
    public void Forbidden403_RateLimitBody_ClassifiesAsRateLimited()
    {
        var classifier = new GitLabResponseClassifier();
        const string body = """{"message":"You have exceeded the rate limit. Throttled."}""";
        using var response = Response(HttpStatusCode.Forbidden, body);
        var result = classifier.ClassifyResponse(response, body);
        Assert.Equal(ProviderOutcomeKind.RateLimited, result.Outcome);
        Assert.NotNull(result.RetryAfter);
    }

    [Fact]
    public void TooManyRequests429_WithRetryAfterHeader_UsesHeaderDelay()
    {
        var classifier = new GitLabResponseClassifier();
        using var response = Response(HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "120"));
        var result = classifier.ClassifyResponse(response, "{}");
        Assert.Equal(ProviderOutcomeKind.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
    }

    [Fact]
    public void TooManyRequests429_WithRateLimitResetHeader_UsesResetDelay()
    {
        var classifier = new GitLabResponseClassifier();
        var resetEpoch = DateTimeOffset.UtcNow.AddMinutes(3).ToUnixTimeSeconds().ToString();
        using var response = Response(HttpStatusCode.TooManyRequests, "{}", ("RateLimit-Reset", resetEpoch));
        var result = classifier.ClassifyResponse(response, "{}");
        Assert.Equal(ProviderOutcomeKind.RateLimited, result.Outcome);
        Assert.NotNull(result.RetryAfter);
        Assert.True(result.RetryAfter.HasValue && result.RetryAfter.Value > TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void TooManyRequests429_WithoutEvidence_UsesFallbackDelay()
    {
        var classifier = new GitLabResponseClassifier();
        using var response = Response(HttpStatusCode.TooManyRequests);
        var result = classifier.ClassifyResponse(response);
        Assert.Equal(ProviderOutcomeKind.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(1), result.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void BadRequestFamily_ClassifiesAsRequestInvalid(HttpStatusCode status)
    {
        var classifier = new GitLabResponseClassifier();
        using var response = Response(status, """{"message":"invalid"}""");
        var result = classifier.ClassifyResponse(response, """{"message":"invalid"}""");
        Assert.Equal(ProviderOutcomeKind.RequestInvalid, result.Outcome);
    }

    [Fact]
    public void NotFound404_ClassifiesAsResourceMissing()
    {
        var classifier = new GitLabResponseClassifier();
        using var response = Response(HttpStatusCode.NotFound, """{"message":"404 Not Found"}""");
        var result = classifier.ClassifyResponse(response, """{"message":"404 Not Found"}""");
        Assert.Equal(ProviderOutcomeKind.ResourceMissing, result.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void ServerErrorFamily_ClassifiesAsTransient(HttpStatusCode status)
    {
        var classifier = new GitLabResponseClassifier();
        using var response = Response(status);
        var result = classifier.ClassifyResponse(response);
        Assert.Equal(ProviderOutcomeKind.Transient, result.Outcome);
    }

    [Fact]
    public void TimeoutException_ClassifiesAsTransient()
    {
        var classifier = new GitLabResponseClassifier();
        var result = classifier.ClassifyResponse(null, null, new TimeoutException("timed out"));
        Assert.Equal(ProviderOutcomeKind.Transient, result.Outcome);
    }

    [Fact]
    public void Cancellation_ClassifiesAsCancellation()
    {
        var classifier = new GitLabResponseClassifier();
        var result = classifier.ClassifyResponse(null, null, new OperationCanceledException());
        Assert.Equal(ProviderOutcomeKind.Cancellation, result.Outcome);
    }

    [Fact]
    public void InterfaceClassify_OmitsHeaders_StillMapsCoreStatuses()
    {
        ISearchProviderOutcomeClassifier classifier = new GitLabResponseClassifier();
        Assert.Equal(ProviderOutcomeKind.AuthInvalid,
            classifier.Classify(SearchProviderEnum.GitLab, HttpStatusCode.Unauthorized));
        Assert.Equal(ProviderOutcomeKind.ForbiddenScope,
            classifier.Classify(SearchProviderEnum.GitLab, HttpStatusCode.Forbidden));
        Assert.Equal(ProviderOutcomeKind.RateLimited,
            classifier.Classify(SearchProviderEnum.GitLab, HttpStatusCode.TooManyRequests));
        Assert.Equal(ProviderOutcomeKind.Success,
            classifier.Classify(SearchProviderEnum.GitLab, HttpStatusCode.OK));
    }
}
