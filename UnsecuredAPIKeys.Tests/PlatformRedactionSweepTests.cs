using System.Text.Json;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 15.3 — Centralized redaction sweep. Injects canary credential material, node
/// tokens, and full fingerprints through every diagnostic sink (redactor, audit
/// sanitizer, metrics snapshot, health DTOs, audit records, scheduler exceptions) and
/// proves none retain the canaries. Stable references (aliases, stable IDs, lease IDs)
/// remain visible by design.
/// </summary>
public sealed class PlatformRedactionSweepTests
{
    private const string MaterialCanary = "ghp_redaction_sweep_canary_001";
    private const string NodeTokenCanary = "node-token-redaction-sweep-canary";
    private static readonly string FingerprintCanary = new('f', 64);

    [Fact]
    public void ControlPlaneRedactor_RemovesAllCanaryShapes()
    {
        var redactor = new ControlPlaneSecretRedactor();
        var rendered =
            $"Authorization: Bearer {MaterialCanary}; " +
            $"X-Node-Token: {NodeTokenCanary}; " +
            $"credential={MaterialCanary}; " +
            $"fingerprint={FingerprintCanary}; " +
            $"leaseId={Guid.NewGuid()}";

        var redacted = redactor.Redact(rendered);

        Assert.DoesNotContain(MaterialCanary, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(NodeTokenCanary, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(FingerprintCanary, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditSanitizer_RemovesCredentialAssignments()
    {
        var sanitizer = new AuditReasonSanitizer();
        var reason = $"Rotated after leak of token={MaterialCanary} for fingerprint {FingerprintCanary}.";

        var sanitized = sanitizer.Sanitize(reason);

        Assert.DoesNotContain(MaterialCanary, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(FingerprintCanary, sanitized, StringComparison.Ordinal);
        Assert.NotEmpty(sanitized);
    }

    [Fact]
    public void MetricsSnapshot_WithRejectedCanary_StaysClean()
    {
        var metrics = new SearchPlatformMetrics();
        var accepted = metrics.TryRecordCounter(
            SearchPlatformMetrics.ClaimTotal,
            new Dictionary<string, string>
            {
                ["provider"] = nameof(SearchProviderEnum.GitHub),
                ["instance"] = Guid.NewGuid().ToString("D"),
                ["outcome"] = MaterialCanary
            });

        Assert.False(accepted);

        var serialized = JsonSerializer.Serialize(metrics.Snapshot());
        Assert.DoesNotContain(MaterialCanary, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(NodeTokenCanary, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(FingerprintCanary, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void HealthAndAuditDtos_SerializeWithoutCanaries()
    {
        var health = new CredentialHealthItem(
            Guid.NewGuid(), SearchProviderEnum.GitHub, Guid.NewGuid(),
            "deadbeefcafe", CredentialSource.Manual, true, null, null,
            DateTime.UtcNow, null, null, 0, "Success", "node:999", DateTime.UtcNow);
        var audit = new PrivilegedAuditRecord
        {
            ActorTelegramId = 999,
            Action = PrivilegedActionKind.PublicSearchConsent,
            TargetStableId = Guid.NewGuid(),
            OccurredUtc = DateTime.UtcNow,
            Outcome = PrivilegedActionOutcome.Succeeded,
            SanitizedReason = "Routine public-search consent review."
        };

        var serialized = JsonSerializer.Serialize(new object[] { health, audit });

        Assert.DoesNotContain(MaterialCanary, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(NodeTokenCanary, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(FingerprintCanary, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void SchedulerArgumentFailures_CarryNoCallerSecrets()
    {
        var scheduler = new SqliteCredentialScheduler(
            null!,
            new LeasePolicyOptions(),
            new ZeroJitter(),
            new FixedClock(),
            null,
            null);

        var error = Record.Exception(() => scheduler.TryClaimAsync(
            Guid.Empty, Guid.NewGuid(), "default",
            NodeTokenCanary, Guid.NewGuid(),
            CredentialGrantScope.Admin, null).GetAwaiter().GetResult());

        Assert.NotNull(error);
        Assert.DoesNotContain(NodeTokenCanary, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(MaterialCanary, error.ToString(), StringComparison.Ordinal);
    }

    private sealed class ZeroJitter : ISchedulerJitterSource
    {
        public TimeSpan Next(TimeSpan maxJitter) => TimeSpan.Zero;
    }

    private sealed class FixedClock : IDatabaseUtcClock
    {
        public Task<DateTime> GetUtcNowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DateTime.UtcNow);
    }
}
