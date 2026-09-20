using Microsoft.EntityFrameworkCore;
using UnsecuredAPIKeys.Data;

namespace UnsecuredAPIKeys.Services;

/// <summary>Configured bounded-retention windows.</summary>
public sealed record PlatformRetentionOptions
{
    /// <summary>How long terminal Claim records survive for idempotent replay.</summary>
    public TimeSpan ClaimTerminalRetention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How long privileged audit records survive.</summary>
    public TimeSpan AuditRetention { get; init; } = TimeSpan.FromDays(90);
}

/// <summary>Counts removed by one retention pass.</summary>
public sealed record PlatformRetentionResult(
    int ExpiredTerminalClaimsRemoved,
    int ExpiredAuditRecordsRemoved);

/// <summary>
/// Bounded audit and Claim retention (Wave 15, Task 15.4). Removes only records outside
/// their windows: non-terminal Claims, terminal Claims inside the terminal retry window
/// (or without terminal evidence, fail-safe), and in-window audit records are always
/// preserved. Bulk deletes keep release-load volume bounded (AC-14.39, AC-17.49).
/// </summary>
public sealed class PlatformRetentionService(
    DBContext dbContext,
    PlatformRetentionOptions options,
    IDatabaseUtcClock clock)
{
    public async Task<PlatformRetentionResult> PurgeAsync(
        CancellationToken cancellationToken = default)
    {
        var now = await clock.GetUtcNowAsync(cancellationToken);
        var claimCutoff = now - options.ClaimTerminalRetention;
        var auditCutoff = now - options.AuditRetention;

        var expiredClaims = await dbContext.CredentialClaimRecords
            .Where(record =>
                record.IsTerminal &&
                record.TerminalizedUtc != null &&
                record.TerminalizedUtc < claimCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var expiredAudits = await dbContext.PrivilegedAuditRecords
            .Where(record => record.OccurredUtc < auditCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        return new PlatformRetentionResult(expiredClaims, expiredAudits);
    }
}
