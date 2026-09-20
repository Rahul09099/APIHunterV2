using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Tests.Infrastructure;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Task 4.3 vertical coverage for audited administrator credential management.
///
/// **Validates: Requirements 1.18-1.20, 1.24, 3.2-3.3, 3.33-3.36,
/// 4.25-4.28, 14.10-14.13, 14.15-14.16**
/// </summary>
public sealed class CredentialManagementCommandTests(SharedMemorySqliteWebApiFixture fixture)
    : IClassFixture<SharedMemorySqliteWebApiFixture>
{
    private static long _telegramId = 8_430_000_000;

    [Fact]
    public async Task DisableAndReenable_CommitStateAndSecretFreeAuditAtomically()
    {
        var seeded = await SeedCredentialAsync(
            new[] { (CredentialGrantScope.User, (long?)Interlocked.Increment(ref _telegramId)) });
        var actorId = Interlocked.Increment(ref _telegramId);
        var actor = SchedulerPrincipal.ForTelegram(actorId, isAdministrator: true);
        const string credentialCanary = "ghp_task_4_3_audit_canary";

        using (var scope = fixture.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICredentialManagementService>();
            var disabled = await service.DisableAsync(
                seeded.StableId,
                actor,
                $"Routine disable token={credentialCanary}");
            Assert.Equal(CredentialManagementStatus.Succeeded, disabled.Status);

            var context = scope.ServiceProvider.GetRequiredService<DBContext>();
            context.ChangeTracker.Clear();
            var credential = await context.SearchProviderTokens
                .AsNoTracking()
                .SingleAsync(candidate => candidate.StableId == seeded.StableId);
            var audit = await context.PrivilegedAuditRecords
                .AsNoTracking()
                .SingleAsync(record =>
                    record.TargetStableId == seeded.StableId &&
                    record.Action == PrivilegedActionKind.CredentialDisable);

            Assert.False(credential.IsEnabled);
            Assert.Equal("AdministratorDisabled", credential.DisabledReason);
            Assert.Equal(disabled.OccurredUtc, credential.DisabledAtUtc);
            Assert.Equal(actorId, audit.ActorTelegramId);
            Assert.Equal(disabled.OccurredUtc, audit.OccurredUtc);
            Assert.DoesNotContain(credentialCanary, audit.SanitizedReason, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", audit.SanitizedReason, StringComparison.Ordinal);
        }

        using (var scope = fixture.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICredentialManagementService>();
            var enabled = await service.ReenableAsync(
                seeded.StableId,
                actor,
                "Operator verified unchanged credential identity");
            Assert.Equal(CredentialManagementStatus.Succeeded, enabled.Status);

            var context = scope.ServiceProvider.GetRequiredService<DBContext>();
            context.ChangeTracker.Clear();
            var credential = await context.SearchProviderTokens
                .AsNoTracking()
                .SingleAsync(candidate => candidate.StableId == seeded.StableId);
            var audit = await context.PrivilegedAuditRecords
                .AsNoTracking()
                .SingleAsync(record =>
                    record.TargetStableId == seeded.StableId &&
                    record.Action == PrivilegedActionKind.CredentialReenable);

            Assert.True(credential.IsEnabled);
            Assert.Null(credential.DisabledReason);
            Assert.Null(credential.DisabledAtUtc);
            Assert.Equal(seeded.Fingerprint, credential.Fingerprint);
            Assert.Equal(actorId, audit.ActorTelegramId);
            Assert.Equal(enabled.OccurredUtc, audit.OccurredUtc);
        }
    }

    [Fact]
    public async Task Replacement_CreatesProtectedIdentityArchivesOldAndAuditsBothStableIds()
    {
        var userPrincipalId = Interlocked.Increment(ref _telegramId);
        var seeded = await SeedCredentialAsync(
            new[]
            {
                (CredentialGrantScope.User, (long?)userPrincipalId),
                (CredentialGrantScope.Admin, (long?)null)
            });
        var actorId = Interlocked.Increment(ref _telegramId);
        var actor = SchedulerPrincipal.ForTelegram(actorId, isAdministrator: true);
        const string replacementMaterial = "ghp_task_4_3_replacement_canary";
        const string auditCanary = "glpat-task-4-3-must-not-reach-audit";

        Guid replacementStableId;
        using (var scope = fixture.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICredentialManagementService>();
            var result = await service.ReplaceAsync(
                seeded.StableId,
                replacementMaterial,
                actor,
                $"Approved rotation PRIVATE-TOKEN={auditCanary}");

            Assert.Equal(CredentialManagementStatus.Succeeded, result.Status);
            replacementStableId = Assert.IsType<Guid>(result.ReplacementStableId);
            Assert.NotEqual(Guid.Empty, replacementStableId);
            Assert.NotEqual(seeded.StableId, replacementStableId);
        }

        using var verificationScope = fixture.Services.CreateScope();
        var context = verificationScope.ServiceProvider.GetRequiredService<DBContext>();
        var rows = await context.SearchProviderTokens
            .AsNoTracking()
            .Include(credential => credential.CredentialGrants)
            .Where(credential => credential.StableId == seeded.StableId || credential.StableId == replacementStableId)
            .ToListAsync();
        var oldCredential = Assert.Single(rows, credential => credential.StableId == seeded.StableId);
        var replacement = Assert.Single(rows, credential => credential.StableId == replacementStableId);

        Assert.False(oldCredential.IsEnabled);
        Assert.Equal("Replaced", oldCredential.DisabledReason);
        Assert.NotNull(oldCredential.DisabledAtUtc);
        Assert.True(oldCredential.IsArchived);
        Assert.Equal(replacementStableId, oldCredential.ReplacedByStableId);
        Assert.True(replacement.IsEnabled);
        Assert.False(replacement.IsArchived);
        Assert.Equal(oldCredential.ProviderInstanceId, replacement.ProviderInstanceId);
        Assert.NotEqual(oldCredential.Fingerprint, replacement.Fingerprint);
        Assert.NotEqual(seeded.Fingerprint, replacement.Fingerprint);
        Assert.True(CredentialStorageService.IsProtected(replacement));
        Assert.Equal(string.Empty, replacement.Token);
        Assert.Equal(
            GrantKeys(oldCredential.CredentialGrants),
            GrantKeys(replacement.CredentialGrants));

        var audits = await context.PrivilegedAuditRecords
            .AsNoTracking()
            .Where(record =>
                record.Action == PrivilegedActionKind.CredentialReplace &&
                (record.TargetStableId == seeded.StableId || record.TargetStableId == replacementStableId))
            .OrderBy(record => record.TargetStableId)
            .ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Equal(new[] { seeded.StableId, replacementStableId }.Order(), audits.Select(record => record.TargetStableId).Order());
        Assert.All(audits, audit =>
        {
            Assert.Equal(actorId, audit.ActorTelegramId);
            Assert.DoesNotContain(replacementMaterial, audit.SanitizedReason, StringComparison.Ordinal);
            Assert.DoesNotContain(auditCanary, audit.SanitizedReason, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", audit.SanitizedReason, StringComparison.Ordinal);
        });

        var materialAccess = verificationScope.ServiceProvider.GetRequiredService<CredentialMaterialAccessService>();
        var operationGrant = await materialAccess.GrantStoredCredentialAfterCommitAsync(
            replacementStableId,
            Guid.NewGuid());
        using var plaintext = await materialAccess.DecryptGrantedAsync(operationGrant);
        Assert.Equal(replacementMaterial, plaintext.Value);
    }

    /// <summary>
    /// **Validates: Requirements 4.25, 4.27**
    /// </summary>
    [Property(MaxTest = 12)]
    public void ReplacementCommand_TransfersEveryDistinctGrantToOnlyTheNewIdentity(PositiveInt input)
    {
        VerifyGrantTransferAsync(input.Get).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task CommandService_RejectsNonAdministratorWithoutMutationOrAudit()
    {
        var seeded = await SeedCredentialAsync(
            new[] { (CredentialGrantScope.Global, (long?)null) });
        var nonAdministrator = SchedulerPrincipal.ForTelegram(
            Interlocked.Increment(ref _telegramId),
            isAdministrator: false);

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICredentialManagementService>();
        await Assert.ThrowsAsync<PrivilegeAuthorizationException>(() =>
            service.DisableAsync(seeded.StableId, nonAdministrator, "Unauthorized attempt"));

        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        context.ChangeTracker.Clear();
        var credential = await context.SearchProviderTokens
            .AsNoTracking()
            .SingleAsync(candidate => candidate.StableId == seeded.StableId);
        Assert.True(credential.IsEnabled);
        Assert.False(await context.PrivilegedAuditRecords.AnyAsync(record =>
            record.TargetStableId == seeded.StableId));
    }

    [Fact]
    public async Task ConfigEndpoints_DisableReenableAndReplaceWithoutReturningSecretOrFingerprint()
    {
        var seeded = await SeedCredentialAsync(
            new[] { (CredentialGrantScope.Admin, (long?)null) });
        var nodeToken = $"task-4-3-admin-{Guid.NewGuid():N}";
        await AddAdministratorNodeAsync(nodeToken);
        const string replacementMaterial = "ghp_task_4_3_config_replacement_canary";

        using (var disable = new HttpRequestMessage(
                   HttpMethod.Delete,
                   $"/api/config/github-token/{seeded.Id}?reason=Routine%20rotation"))
        {
            disable.Headers.Add("X-Node-Token", nodeToken);
            using var response = await fixture.Client.SendAsync(disable);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var reenable = new HttpRequestMessage(
                   HttpMethod.Post,
                   $"/api/config/github-token/{seeded.Id}/reenable")
               {
                   Content = JsonContent.Create(new { reason = "Validation completed" })
               })
        {
            reenable.Headers.Add("X-Node-Token", nodeToken);
            using var response = await fixture.Client.SendAsync(reenable);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var replace = new HttpRequestMessage(
                   HttpMethod.Post,
                   $"/api/config/github-token/{seeded.Id}/replace")
               {
                   Content = JsonContent.Create(new
                   {
                       token = replacementMaterial,
                       reason = "Scheduled rotation"
                   })
               })
        {
            replace.Headers.Add("X-Node-Token", nodeToken);
            using var response = await fixture.Client.SendAsync(replace);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(replacementMaterial, body, StringComparison.Ordinal);
            Assert.DoesNotContain("fingerprint", body, StringComparison.OrdinalIgnoreCase);
            using var json = JsonDocument.Parse(body);
            Assert.NotEqual(Guid.Empty, json.RootElement.GetProperty("replacementStableId").GetGuid());
        }
    }

    [Fact]
    public void ConfigAndTelegramCredentialMutations_DelegateWithoutProtectedFieldWritesOrHardDelete()
    {
        var config = ProviderInstanceTestContract.ReadRepositoryFile(
            "UnsecuredAPIKeys.WebAPI",
            "Controllers",
            "ConfigController.cs");
        var telegram = ProviderInstanceTestContract.ReadRepositoryFile(
            "UnsecuredAPIKeys.Services",
            "Telegram",
            "TelegramBotService.cs");
        var databaseService = ProviderInstanceTestContract.ReadRepositoryFile(
            "UnsecuredAPIKeys.Services",
            "DatabaseService.cs");

        Assert.Contains("ICredentialManagementService", config, StringComparison.Ordinal);
        Assert.Contains("ICredentialManagementService", telegram, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectedCiphertext =", config, StringComparison.Ordinal);
        Assert.DoesNotContain("Fingerprint =", config, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectedCiphertext =", telegram, StringComparison.Ordinal);
        Assert.DoesNotContain("Fingerprint =", telegram, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchProviderTokens.Remove", config, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchProviderTokens.Remove", telegram, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteGitHubTokenAsync", databaseService, StringComparison.Ordinal);
        Assert.DoesNotContain("Received Telegram message: '{Text}'", telegram, StringComparison.Ordinal);
    }

    private async Task VerifyGrantTransferAsync(int seed)
    {
        var grantCount = seed % 4 + 1;
        var grants = Enumerable.Range(0, grantCount)
            .Select(_ => (
                CredentialGrantScope.User,
                (long?)Interlocked.Increment(ref _telegramId)))
            .ToList();
        if ((seed & 1) == 0) grants.Add((CredentialGrantScope.Admin, null));
        if ((seed & 2) == 0) grants.Add((CredentialGrantScope.Global, null));

        var seeded = await SeedCredentialAsync(grants);
        var actor = SchedulerPrincipal.ForTelegram(
            Interlocked.Increment(ref _telegramId),
            isAdministrator: true);
        Guid replacementStableId;
        using (var scope = fixture.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICredentialManagementService>();
            var result = await service.ReplaceAsync(
                seeded.StableId,
                $"ghp_task_4_3_property_{seed}_{Guid.NewGuid():N}",
                actor,
                "Property replacement");
            Assert.Equal(CredentialManagementStatus.Succeeded, result.Status);
            replacementStableId = Assert.IsType<Guid>(result.ReplacementStableId);
        }

        using var verificationScope = fixture.Services.CreateScope();
        var context = verificationScope.ServiceProvider.GetRequiredService<DBContext>();
        var identities = await context.SearchProviderTokens
            .AsNoTracking()
            .Include(credential => credential.CredentialGrants)
            .Where(credential => credential.StableId == seeded.StableId || credential.StableId == replacementStableId)
            .ToListAsync();
        var oldCredential = Assert.Single(identities, credential => credential.StableId == seeded.StableId);
        var replacement = Assert.Single(identities, credential => credential.StableId == replacementStableId);
        Assert.Equal(GrantKeys(oldCredential.CredentialGrants), GrantKeys(replacement.CredentialGrants));

        var unrelated = await CreateUnpersistedCredentialAsync(seed);
        Assert.Empty(unrelated.CredentialGrants);
    }

    private async Task<SearchProviderToken> CreateUnpersistedCredentialAsync(int seed)
    {
        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        var instance = await context.SearchProviderInstances
            .AsNoTracking()
            .SingleAsync(candidate => candidate.StableId == ProviderInstanceSchema.DefaultGitHubStableId);
        var storage = scope.ServiceProvider.GetRequiredService<CredentialStorageService>();
        return storage.CreateProtectedCredential(
            $"ghp_task_4_3_unpersisted_{seed}_{Guid.NewGuid():N}",
            instance);
    }

    private async Task<SeededCredential> SeedCredentialAsync(
        IEnumerable<(CredentialGrantScope Scope, long? TelegramPrincipalId)> grants)
    {
        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        var instance = await context.SearchProviderInstances
            .SingleAsync(candidate => candidate.StableId == ProviderInstanceSchema.DefaultGitHubStableId);
        var storage = scope.ServiceProvider.GetRequiredService<CredentialStorageService>();
        var credential = storage.CreateProtectedCredential(
            $"ghp_task_4_3_seed_{Guid.NewGuid():N}",
            instance,
            CredentialSource.Manual);
        foreach (var grant in grants)
        {
            credential.CredentialGrants.Add(new CredentialGrant
            {
                Scope = grant.Scope,
                TelegramPrincipalId = grant.TelegramPrincipalId,
                CreatedUtc = DateTime.UtcNow
            });
        }

        context.SearchProviderTokens.Add(credential);
        await context.SaveChangesAsync();
        return new SeededCredential(
            credential.Id,
            credential.StableId,
            credential.Fingerprint.ToArray());
    }

    private async Task AddAdministratorNodeAsync(string nodeToken)
    {
        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        var telegramId = Interlocked.Increment(ref _telegramId);
        context.TelegramSubscribers.Add(new TelegramSubscriber
        {
            TelegramId = telegramId,
            Username = $"task_4_3_{telegramId}",
            IsAdmin = true,
            NodeToken = nodeToken,
            SubscriptionExpiryUtc = DateTime.UtcNow.AddDays(1),
            CreatedAtUtc = DateTime.UtcNow,
            LastNodeHeartbeatUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static string[] GrantKeys(IEnumerable<CredentialGrant> grants) =>
        grants
            .Select(grant => $"{grant.Scope}:{grant.TelegramPrincipalId?.ToString() ?? "-"}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private sealed record SeededCredential(int Id, Guid StableId, byte[] Fingerprint);
}
