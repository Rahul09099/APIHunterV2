using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Tests.Infrastructure;
using UnsecuredAPIKeys.WebAPI.Controllers;
using Xunit;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Contract and wired-path coverage for multi-search-provider-platform Task 3.3.
///
/// **Validates: Requirements 1.18-1.24, 2.13, 12.8, 12.14, 14.10, 14.14-14.16**
/// </summary>
public sealed class PrivilegedCommandBoundaryTests(SharedMemorySqliteWebApiFixture fixture)
    : IClassFixture<SharedMemorySqliteWebApiFixture>
{
    private static long _telegramId = 8_300_000_000;

    [Fact]
    public void PrivilegedActionKind_DefinesEveryProtectedCommandExactlyOnce()
    {
        Assert.Equal(
            new[]
            {
                nameof(PrivilegedActionKind.CredentialDisable),
                nameof(PrivilegedActionKind.CredentialReenable),
                nameof(PrivilegedActionKind.CredentialReplace),
                nameof(PrivilegedActionKind.ProviderInstanceApprove),
                nameof(PrivilegedActionKind.PrivateNetworkAllowlist),
                nameof(PrivilegedActionKind.PublicSearchConsent)
            }.Order(StringComparer.Ordinal),
            Enum.GetNames<PrivilegedActionKind>().Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// **Validates: Requirements 1.18, 1.19, 1.20, 1.21, 1.22, 1.23**
    /// </summary>
    [Property(MaxTest = 60)]
    public void EveryPrivilegedAction_RequiresAnAuthenticatedMappedAdministrator(NonNegativeInt selector)
    {
        var actions = Enum.GetValues<PrivilegedActionKind>();
        var action = actions[selector.Get % actions.Length];
        var policy = new PrivilegePolicy();

        Assert.Equal(
            PrivilegeAuthorizationDecision.Unauthenticated,
            policy.Authorize(NodePrincipalResolution.Unauthenticated, action));
        Assert.Equal(
            PrivilegeAuthorizationDecision.Forbidden,
            policy.Authorize(NodePrincipalResolution.AuthenticatedWithoutMapping, action));
        Assert.Equal(
            PrivilegeAuthorizationDecision.Forbidden,
            policy.Authorize(
                NodePrincipalResolution.Resolved(SchedulerPrincipal.ForTelegram(8301, isAdministrator: false)),
                action));
        Assert.Equal(
            PrivilegeAuthorizationDecision.Forbidden,
            policy.Authorize(NodePrincipalResolution.Resolved(SchedulerPrincipal.System), action));
        Assert.Equal(
            PrivilegeAuthorizationDecision.Authorized,
            policy.Authorize(
                NodePrincipalResolution.Resolved(SchedulerPrincipal.ForTelegram(8302, isAdministrator: true)),
                action));
    }

    [Fact]
    public void PrivilegePolicy_FailsClosedForAnUndefinedAction()
    {
        var policy = new PrivilegePolicy();
        var resolution = NodePrincipalResolution.Resolved(
            SchedulerPrincipal.ForTelegram(8303, isAdministrator: true));

        Assert.Equal(
            PrivilegeAuthorizationDecision.Forbidden,
            policy.Authorize(resolution, (PrivilegedActionKind)int.MaxValue));
    }

    [Fact]
    public void AuditReasonSanitizer_BoundsTextAndRemovesControlPlaneSecrets()
    {
        const string nodeCanary = "node-canary-3-3";
        const string providerCanary = "ghp_provider_canary_3_3";
        const string fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var sanitizer = new AuditReasonSanitizer();

        var sanitized = sanitizer.Sanitize(
            $"Routine approval\r\nX-Node-Token: {nodeCanary} Authorization: Bearer {providerCanary} " +
            $"Fingerprint={fingerprint} {new string('x', AuditReasonSanitizer.MaximumLength + 50)}");

        Assert.StartsWith("Routine approval", sanitized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(nodeCanary, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(providerCanary, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(fingerprint, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', sanitized);
        Assert.DoesNotContain('\n', sanitized);
        Assert.InRange(sanitized.Length, 1, AuditReasonSanitizer.MaximumLength);
    }

    [Fact]
    public async Task CommandService_RejectsNonAdministratorEvenWhenControllerIsBypassed()
    {
        var stableId = await AddProviderInstanceAsync();
        var nonAdministrator = SchedulerPrincipal.ForTelegram(
            Interlocked.Increment(ref _telegramId),
            isAdministrator: false);

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProviderInstanceCommandService>();

        await Assert.ThrowsAsync<PrivilegeAuthorizationException>(() =>
            service.ApproveAsync(stableId, nonAdministrator, "Attempted direct service call"));

        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        context.ChangeTracker.Clear();
        var instance = await context.SearchProviderInstances
            .AsNoTracking()
            .SingleAsync(candidate => candidate.StableId == stableId);
        Assert.Null(instance.ApprovedByTelegramId);
        Assert.False(await context.PrivilegedAuditRecords.AnyAsync(record => record.TargetStableId == stableId));
    }

    [Fact]
    public async Task ApprovalEndpoint_DistinguishesUnauthenticatedAndNonAdministratorWithoutMutation()
    {
        var stableId = await AddProviderInstanceAsync();
        var nonAdminToken = $"task-3-3-user-{Guid.NewGuid():N}";
        await AddNodeAsync(nonAdminToken, isAdministrator: false);

        using var missingAuthentication = await fixture.Client.PostAsJsonAsync(
            $"/api/provider-instances/{stableId:D}/approval",
            new { reason = "Missing authentication" });
        Assert.Equal(HttpStatusCode.Unauthorized, missingAuthentication.StatusCode);

        using var forbiddenRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/provider-instances/{stableId:D}/approval")
        {
            Content = JsonContent.Create(new { reason = "Not an administrator" })
        };
        forbiddenRequest.Headers.Add("X-Node-Token", nonAdminToken);
        using var forbiddenResponse = await fixture.Client.SendAsync(forbiddenRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);

        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        var instance = await context.SearchProviderInstances
            .AsNoTracking()
            .SingleAsync(candidate => candidate.StableId == stableId);
        Assert.Null(instance.ApprovedByTelegramId);
        Assert.False(await context.PrivilegedAuditRecords.AnyAsync(record => record.TargetStableId == stableId));
    }

    [Fact]
    public async Task ApprovalEndpoint_PersistsProviderApprovalAndSanitizedAuditUsingDatabaseUtc()
    {
        const string nodeCanary = "node-canary-end-to-end-3-3";
        const string providerCanary = "glpat-provider-canary-end-to-end-3-3";
        const string fingerprint = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        var stableId = await AddProviderInstanceAsync();
        var adminToken = $"task-3-3-admin-{Guid.NewGuid():N}";
        var adminTelegramId = await AddNodeAsync(adminToken, isAdministrator: true);
        var databaseTimeBefore = await ReadDatabaseUtcAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/provider-instances/{stableId:D}/approval")
        {
            Content = JsonContent.Create(new
            {
                reason = $"Approved for routine use\r\nX-Node-Token: {nodeCanary} " +
                         $"PRIVATE-TOKEN={providerCanary} Fingerprint={fingerprint}"
            })
        };
        request.Headers.Add("X-Node-Token", adminToken);

        using var response = await fixture.Client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(nodeCanary, responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(providerCanary, responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(fingerprint, responseBody, StringComparison.Ordinal);

        var databaseTimeAfter = await ReadDatabaseUtcAsync();
        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        var instance = await context.SearchProviderInstances
            .AsNoTracking()
            .SingleAsync(candidate => candidate.StableId == stableId);
        var audit = await context.PrivilegedAuditRecords
            .AsNoTracking()
            .SingleAsync(record => record.TargetStableId == stableId);

        Assert.Equal(adminTelegramId, instance.ApprovedByTelegramId);
        Assert.Equal(adminTelegramId, audit.ActorTelegramId);
        Assert.Equal(PrivilegedActionKind.ProviderInstanceApprove, audit.Action);
        Assert.Equal(stableId, audit.TargetStableId);
        Assert.Equal(PrivilegedActionOutcome.Succeeded, audit.Outcome);
        Assert.Equal(DateTimeKind.Utc, audit.OccurredUtc.Kind);
        Assert.InRange(
            audit.OccurredUtc,
            databaseTimeBefore.AddSeconds(-1),
            databaseTimeAfter.AddSeconds(1));
        Assert.DoesNotContain(nodeCanary, audit.SanitizedReason, StringComparison.Ordinal);
        Assert.DoesNotContain(providerCanary, audit.SanitizedReason, StringComparison.Ordinal);
        Assert.DoesNotContain(fingerprint, audit.SanitizedReason, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", audit.SanitizedReason, StringComparison.Ordinal);
        Assert.Equal(audit.OccurredUtc, instance.UpdatedUtc);
    }

    [Fact]
    public void ProviderInstanceController_CannotMutateEfStateDirectly()
    {
        var constructors = typeof(ProviderInstancesController).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(constructors);
        Assert.All(
            constructors,
            constructor => Assert.DoesNotContain(
                constructor.GetParameters(),
                parameter => parameter.ParameterType == typeof(DBContext)));
        Assert.Contains(
            constructors.SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(IProviderInstanceCommandService));
    }

    [Fact]
    public void PrivilegedAuditModel_ContainsRequiredFieldsIndexAndEverySchemaSurface()
    {
        var options = new DbContextOptionsBuilder<DBContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new DBContext(options);
        var entity = context.Model.FindEntityType(typeof(PrivilegedAuditRecord));
        Assert.NotNull(entity);

        foreach (var propertyName in new[]
                 {
                     nameof(PrivilegedAuditRecord.ActorTelegramId),
                     nameof(PrivilegedAuditRecord.Action),
                     nameof(PrivilegedAuditRecord.TargetStableId),
                     nameof(PrivilegedAuditRecord.OccurredUtc),
                     nameof(PrivilegedAuditRecord.Outcome),
                     nameof(PrivilegedAuditRecord.SanitizedReason)
                 })
        {
            Assert.NotNull(entity!.FindProperty(propertyName));
        }

        Assert.Contains(
            entity!.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(PrivilegedAuditRecord.TargetStableId), nameof(PrivilegedAuditRecord.OccurredUtc) }));

        var databaseService = ProviderInstanceTestContract.ReadRepositoryFile(
            "UnsecuredAPIKeys.Services",
            "DatabaseService.cs");
        var masterInit = ProviderInstanceTestContract.ReadRepositoryFile("master_init.sql");
        var readiness = ProviderInstanceTestContract.ReadRepositoryFile(
            "UnsecuredAPIKeys.Services",
            "ProviderInstanceReadinessService.cs");

        Assert.True(
            databaseService.Split("PrivilegedAuditRecords", StringSplitOptions.None).Length >= 3,
            "Both manual SQLite and PostgreSQL schema paths must create PrivilegedAuditRecords.");
        Assert.Contains("PrivilegedAuditRecords", masterInit, StringComparison.Ordinal);
        Assert.Contains("PrivilegedAuditRecords", readiness, StringComparison.Ordinal);
        Assert.Contains("IX_PrivilegedAuditRecords_TargetStableId_OccurredUtc", databaseService, StringComparison.Ordinal);
        Assert.Contains("IX_PrivilegedAuditRecords_TargetStableId_OccurredUtc", masterInit, StringComparison.Ordinal);
        Assert.Contains("IX_PrivilegedAuditRecords_TargetStableId_OccurredUtc", readiness, StringComparison.Ordinal);
    }

    private async Task<Guid> AddProviderInstanceAsync()
    {
        var stableId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        context.SearchProviderInstances.Add(new SearchProviderInstance
        {
            StableId = stableId,
            ProviderKind = SearchProviderEnum.GitHub,
            DisplayName = $"Task 3.3 {stableId:N}",
            NormalizedScheme = "https",
            NormalizedHost = $"task-3-3-{stableId:N}.example.test",
            NormalizedPort = 443,
            NormalizedBasePath = "/api/v1",
            IsEnabled = true,
            AllowGlobalPublicSearch = false,
            MaxConcurrentOperations = 1,
            SettingsVersion = 1,
            SettingsJson = "{}",
            ApprovedByTelegramId = null,
            CreatedUtc = now,
            UpdatedUtc = now
        });
        await context.SaveChangesAsync();
        return stableId;
    }

    private async Task<long> AddNodeAsync(string nodeToken, bool isAdministrator)
    {
        var telegramId = Interlocked.Increment(ref _telegramId);
        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        context.TelegramSubscribers.Add(new TelegramSubscriber
        {
            TelegramId = telegramId,
            Username = $"task_3_3_{telegramId}",
            IsAdmin = isAdministrator,
            SubscriptionExpiryUtc = DateTime.UtcNow.AddDays(1),
            CreatedAtUtc = DateTime.UtcNow,
            NodeToken = nodeToken,
            LastNodeHeartbeatUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return telegramId;
    }

    private async Task<DateTime> ReadDatabaseUtcAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        var connection = context.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CURRENT_TIMESTAMP";
            var value = await command.ExecuteScalarAsync();
            var parsed = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }
}
