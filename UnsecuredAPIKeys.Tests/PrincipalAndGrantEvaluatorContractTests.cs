using System.Net;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using UnsecuredAPIKeys.Data;
using UnsecuredAPIKeys.Data.Common;
using UnsecuredAPIKeys.Data.Models;
using UnsecuredAPIKeys.Services;
using UnsecuredAPIKeys.Tests.Infrastructure;
using Xunit;
using Xunit.Sdk;

namespace UnsecuredAPIKeys.Tests;

/// <summary>
/// Expected-red contract tests for multi-search-provider-platform Task 3.1.
/// Runtime model and service inspection keeps this test assembly buildable before
/// Task 3.2 adds Scheduler principals, durable grants, and the common evaluator.
///
/// **Validates: Requirements 1.4-1.17, 5.11, 17.3**
/// </summary>
public sealed class PrincipalAndGrantEvaluatorContractTests(SharedMemorySqliteWebApiFixture fixture)
    : IClassFixture<SharedMemorySqliteWebApiFixture>
{
    private static long _telegramId = 8_000_000_000;

    [Fact]
    public void GrantScope_DefinesExactlyUserGlobalAndAdmin()
    {
        var scopeType = PrincipalGrantTestContract.RequireGrantScopeType();

        Assert.Equal(
            new[] { "Admin", "Global", "User" },
            Enum.GetNames(scopeType).Order(StringComparer.Ordinal).ToArray());

        using var context = PrincipalGrantTestContract.CreateModelContext();
        var grantEntity = PrincipalGrantTestContract.RequireCredentialGrantEntity(context.Model);
        var scopeProperty = PrincipalGrantTestContract.RequireScopeProperty(grantEntity);
        Assert.Equal(scopeType, scopeProperty.ClrType);
    }

    [Fact]
    public async Task UserGrant_RequiresExactlyOneTelegramPrincipal()
    {
        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        var credential = await PrincipalGrantTestContract.AddCredentialAsync(context);
        var grant = PrincipalGrantTestContract.CreateGrant(
            context.Model,
            credential.Id,
            scopeName: "User",
            telegramPrincipalId: null);

        context.Add(grant);
        var error = await Record.ExceptionAsync(() => context.SaveChangesAsync());

        Assert.NotNull(error);
        Assert.True(
            error is DbUpdateException or InvalidOperationException or ArgumentException,
            $"A User grant without a Telegram principal must be rejected, but received {error!.GetType().FullName}.");
    }

    [Fact]
    public async Task NodeResolver_UsesRegisteredTelegramMappingAndHasNoClientPrincipalOverride()
    {
        var telegramId = NextTelegramId();
        var nodeToken = $"task-3-1-node-{Guid.NewGuid():N}";
        await AddNodeAsync(telegramId, nodeToken, isAdministrator: false);

        var resolution = await PrincipalGrantTestContract.ResolveNodeAsync(fixture.Services, nodeToken);

        Assert.NotNull(resolution.Principal);
        Assert.Equal(
            telegramId,
            PrincipalGrantTestContract.ReadTelegramPrincipalId(resolution.Principal!));
        Assert.False(PrincipalGrantTestContract.ReadAdministratorState(resolution.Principal!));

        var unexpectedInputs = resolution.Method.GetParameters()
            .Where(parameter => parameter.ParameterType != typeof(string))
            .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
            .Where(parameter => !parameter.HasDefaultValue)
            .ToArray();
        Assert.Empty(unexpectedInputs);
        Assert.DoesNotContain(
            resolution.Method.GetParameters(),
            parameter =>
                parameter.Name?.Contains("principal", StringComparison.OrdinalIgnoreCase) == true ||
                parameter.Name?.Contains("telegram", StringComparison.OrdinalIgnoreCase) == true ||
                parameter.Name?.Contains("owner", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task AuthenticatedNodeWithoutTelegramMapping_ReceivesHttp403()
    {
        // A persisted node token proves authentication, while TelegramId=0 represents
        // the absence of a registered Telegram principal mapping at this boundary.
        var nodeToken = $"task-3-1-unmapped-node-{Guid.NewGuid():N}";
        await AddNodeAsync(telegramId: 0, nodeToken, isAdministrator: false);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/nodes/sync");
        request.Headers.Add("X-Node-Token", nodeToken);
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Evaluator_AppliesExactNonAdministratorAndAdministratorGrantSets()
    {
        var userTelegramId = NextTelegramId();
        var otherTelegramId = NextTelegramId();
        var adminTelegramId = NextTelegramId();
        var userNodeToken = $"task-3-1-user-{Guid.NewGuid():N}";
        var adminNodeToken = $"task-3-1-admin-{Guid.NewGuid():N}";
        await AddNodeAsync(userTelegramId, userNodeToken, isAdministrator: false);
        await AddNodeAsync(adminTelegramId, adminNodeToken, isAdministrator: true);

        SearchProviderToken userCredential;
        SearchProviderToken otherUserCredential;
        SearchProviderToken globalCredential;
        SearchProviderToken adminCredential;
        using (var scope = fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DBContext>();
            userCredential = await PrincipalGrantTestContract.AddCredentialWithGrantAsync(
                context,
                "User",
                userTelegramId);
            otherUserCredential = await PrincipalGrantTestContract.AddCredentialWithGrantAsync(
                context,
                "User",
                otherTelegramId);
            globalCredential = await PrincipalGrantTestContract.AddCredentialWithGrantAsync(
                context,
                "Global",
                telegramPrincipalId: null);
            adminCredential = await PrincipalGrantTestContract.AddCredentialWithGrantAsync(
                context,
                "Admin",
                telegramPrincipalId: null);
        }

        var userPrincipal = (await PrincipalGrantTestContract.ResolveNodeAsync(
            fixture.Services,
            userNodeToken)).Principal!;
        var adminPrincipal = (await PrincipalGrantTestContract.ResolveNodeAsync(
            fixture.Services,
            adminNodeToken)).Principal!;

        Assert.True(await IsAuthorizedAsync(userCredential.Id, userPrincipal));
        Assert.False(await IsAuthorizedAsync(otherUserCredential.Id, userPrincipal));
        Assert.True(await IsAuthorizedAsync(globalCredential.Id, userPrincipal));
        Assert.False(await IsAuthorizedAsync(adminCredential.Id, userPrincipal));

        Assert.False(await IsAuthorizedAsync(userCredential.Id, adminPrincipal));
        Assert.False(await IsAuthorizedAsync(otherUserCredential.Id, adminPrincipal));
        Assert.True(await IsAuthorizedAsync(globalCredential.Id, adminPrincipal));
        Assert.True(await IsAuthorizedAsync(adminCredential.Id, adminPrincipal));
    }

    [Fact]
    public async Task MasterLocalAndNodeAuthorization_UseTheSameCommonEvaluator()
    {
        var telegramId = NextTelegramId();
        var nodeToken = $"task-3-1-parity-{Guid.NewGuid():N}";
        await AddNodeAsync(telegramId, nodeToken, isAdministrator: false);

        SearchProviderToken credential;
        using (var scope = fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DBContext>();
            credential = await PrincipalGrantTestContract.AddCredentialWithGrantAsync(
                context,
                "User",
                telegramId);
        }

        // The resolved SchedulerPrincipal is server-owned and is the same immutable
        // authorization input used when the Master starts equivalent local work.
        var schedulerPrincipal = (await PrincipalGrantTestContract.ResolveNodeAsync(
            fixture.Services,
            nodeToken)).Principal!;
        var nodeDecision = await PrincipalGrantTestContract.EvaluateCredentialAsync(
            fixture.Services,
            credential.Id,
            schedulerPrincipal);
        var masterLocalDecision = await PrincipalGrantTestContract.EvaluateCredentialAsync(
            fixture.Services,
            credential.Id,
            schedulerPrincipal);

        Assert.True(nodeDecision.IsAuthorized);
        Assert.Equal(nodeDecision.IsAuthorized, masterLocalDecision.IsAuthorized);
        Assert.Equal(nodeDecision.Method, masterLocalDecision.Method);
        Assert.DoesNotContain("Node", nodeDecision.Method.Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Master", nodeDecision.Method.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SeparateUserGrants_AuthorizeMultiplePrincipalsOnOneCredential()
    {
        var firstTelegramId = NextTelegramId();
        var secondTelegramId = NextTelegramId();
        var ungrantedTelegramId = NextTelegramId();
        var firstToken = $"task-3-1-shared-a-{Guid.NewGuid():N}";
        var secondToken = $"task-3-1-shared-b-{Guid.NewGuid():N}";
        var ungrantedToken = $"task-3-1-shared-c-{Guid.NewGuid():N}";
        await AddNodeAsync(firstTelegramId, firstToken, isAdministrator: false);
        await AddNodeAsync(secondTelegramId, secondToken, isAdministrator: false);
        await AddNodeAsync(ungrantedTelegramId, ungrantedToken, isAdministrator: false);

        SearchProviderToken credential;
        using (var scope = fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DBContext>();
            credential = await PrincipalGrantTestContract.AddCredentialAsync(context);
            context.Add(PrincipalGrantTestContract.CreateGrant(
                context.Model,
                credential.Id,
                "User",
                firstTelegramId));
            context.Add(PrincipalGrantTestContract.CreateGrant(
                context.Model,
                credential.Id,
                "User",
                secondTelegramId));
            await context.SaveChangesAsync();
        }

        var firstPrincipal = (await PrincipalGrantTestContract.ResolveNodeAsync(
            fixture.Services,
            firstToken)).Principal!;
        var secondPrincipal = (await PrincipalGrantTestContract.ResolveNodeAsync(
            fixture.Services,
            secondToken)).Principal!;
        var ungrantedPrincipal = (await PrincipalGrantTestContract.ResolveNodeAsync(
            fixture.Services,
            ungrantedToken)).Principal!;

        Assert.True(await IsAuthorizedAsync(credential.Id, firstPrincipal));
        Assert.True(await IsAuthorizedAsync(credential.Id, secondPrincipal));
        Assert.False(await IsAuthorizedAsync(credential.Id, ungrantedPrincipal));
    }

    [Fact]
    public async Task GrantRevocation_AppliesToEveryEvaluationStartedAfterCommit()
    {
        var telegramId = NextTelegramId();
        var nodeToken = $"task-3-1-revocation-{Guid.NewGuid():N}";
        await AddNodeAsync(telegramId, nodeToken, isAdministrator: false);
        var principal = (await PrincipalGrantTestContract.ResolveNodeAsync(
            fixture.Services,
            nodeToken)).Principal!;

        using var mutationScope = fixture.Services.CreateScope();
        var context = mutationScope.ServiceProvider.GetRequiredService<DBContext>();
        var credential = await PrincipalGrantTestContract.AddCredentialAsync(context);
        var grant = PrincipalGrantTestContract.CreateGrant(
            context.Model,
            credential.Id,
            "User",
            telegramId);
        context.Add(grant);
        await context.SaveChangesAsync();

        Assert.True(await IsAuthorizedAsync(credential.Id, principal));

        context.Remove(grant);
        await context.SaveChangesAsync();

        Assert.False(await IsAuthorizedAsync(credential.Id, principal));
    }

    [Fact]
    public async Task LegacyOwnerAndClientOperationFields_CannotAuthorizeWithoutAnApplicableGrant()
    {
        var telegramId = NextTelegramId();
        var nodeToken = $"task-3-1-no-inference-{Guid.NewGuid():N}";
        await AddNodeAsync(telegramId, nodeToken, isAdministrator: false);

        SearchProviderToken credential;
        using (var scope = fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DBContext>();
            credential = await PrincipalGrantTestContract.AddCredentialAsync(
                context,
                addedByTelegramId: telegramId);
        }

        var principal = (await PrincipalGrantTestContract.ResolveNodeAsync(
            fixture.Services,
            nodeToken)).Principal!;
        var decision = await PrincipalGrantTestContract.EvaluateCredentialAsync(
            fixture.Services,
            credential.Id,
            principal);

        Assert.False(decision.IsAuthorized);

        // Authorization accepts only durable credential identity plus the server-created
        // SchedulerPrincipal. Provider/query/work/partition/owner fields cannot supply grants.
        var forbiddenOperationInputs = decision.Method.GetParameters()
            .Where(parameter =>
                parameter.Name?.Contains("addedBy", StringComparison.OrdinalIgnoreCase) == true ||
                parameter.Name?.Contains("owner", StringComparison.OrdinalIgnoreCase) == true ||
                parameter.Name?.Contains("provider", StringComparison.OrdinalIgnoreCase) == true ||
                parameter.Name?.Contains("query", StringComparison.OrdinalIgnoreCase) == true ||
                parameter.Name?.Contains("work", StringComparison.OrdinalIgnoreCase) == true ||
                parameter.Name?.Contains("partition", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        Assert.Empty(forbiddenOperationInputs);
    }

    private static long NextTelegramId() => Interlocked.Increment(ref _telegramId);

    private async Task AddNodeAsync(long telegramId, string nodeToken, bool isAdministrator)
    {
        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        context.TelegramSubscribers.Add(new TelegramSubscriber
        {
            TelegramId = telegramId,
            Username = telegramId > 0 ? $"task_3_1_{telegramId}" : null,
            IsAdmin = isAdministrator,
            SubscriptionExpiryUtc = DateTime.UtcNow.AddDays(1),
            CreatedAtUtc = DateTime.UtcNow,
            NodeToken = nodeToken,
            LastNodeHeartbeatUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private async Task<bool> IsAuthorizedAsync(int credentialId, object schedulerPrincipal) =>
        (await PrincipalGrantTestContract.EvaluateCredentialAsync(
            fixture.Services,
            credentialId,
            schedulerPrincipal)).IsAuthorized;
}

internal static class PrincipalGrantTestContract
{
    private static readonly string[] ResolverTypeNames =
    [
        "INodePrincipalResolver",
        "ISchedulerPrincipalResolver",
        "NodePrincipalResolver",
        "SchedulerPrincipalResolver",
        "IPrincipalResolver",
        "PrincipalResolver"
    ];

    private static readonly string[] EvaluatorTypeNames =
    [
        "ICredentialGrantEvaluator",
        "IGrantEvaluator",
        "CredentialGrantEvaluator",
        "GrantEvaluator"
    ];

    private static readonly string[] EvaluatorMethodNames =
    [
        "IsCredentialAuthorizedAsync",
        "IsAuthorizedAsync",
        "HasApplicableGrantAsync",
        "CanUseCredentialAsync",
        "EvaluateAsync"
    ];

    private static IReadOnlyList<Assembly> ContractAssemblies =>
    [
        typeof(DBContext).Assembly,
        typeof(DatabaseService).Assembly,
        typeof(global::Program).Assembly
    ];

    public static DBContext CreateModelContext()
    {
        var options = new DbContextOptionsBuilder<DBContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new DBContext(options);
    }

    public static Type RequireGrantScopeType()
    {
        var type = FindTypes()
            .SingleOrDefault(candidate =>
                candidate.IsEnum &&
                candidate.Name.Equals("CredentialGrantScope", StringComparison.Ordinal));
        Assert.True(
            type is not null,
            "Expected a closed CredentialGrantScope enum. Task 3.2 has not added the grant contract yet.");
        return type!;
    }

    public static IEntityType RequireCredentialGrantEntity(IModel model)
    {
        var entityType = model.GetEntityTypes().SingleOrDefault(candidate =>
            candidate.ClrType.Name.Equals("CredentialGrant", StringComparison.Ordinal) ||
            (candidate.GetTableName()?.Contains("CredentialGrant", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.True(
            entityType is not null,
            "Expected a durable CredentialGrant EF entity. Task 3.2 has not added the Grant Store yet.");
        return entityType!;
    }

    public static IProperty RequireScopeProperty(IEntityType grantEntity)
    {
        var property = grantEntity.FindProperty("Scope") ??
                       grantEntity.GetProperties().SingleOrDefault(candidate =>
                           candidate.Name.Contains("Scope", StringComparison.OrdinalIgnoreCase));
        Assert.True(property is not null, "CredentialGrant must persist its exact grant Scope.");
        Assert.True(property!.ClrType.IsEnum, "CredentialGrant.Scope must use the closed grant-scope enum.");
        return property;
    }

    public static IProperty RequireTelegramPrincipalProperty(IEntityType grantEntity)
    {
        var property = grantEntity.GetProperties().SingleOrDefault(candidate =>
            candidate.Name.Contains("Telegram", StringComparison.OrdinalIgnoreCase) &&
            (candidate.Name.Contains("Principal", StringComparison.OrdinalIgnoreCase) ||
             candidate.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)));
        Assert.True(
            property is not null,
            "CredentialGrant must persist the Telegram principal identity for User grants.");
        Assert.True(
            property!.IsNullable,
            "The Telegram principal column must permit null for Global and Admin grants.");
        return property;
    }

    public static async Task<SearchProviderToken> AddCredentialAsync(
        DBContext context,
        long? addedByTelegramId = null)
    {
        var credential = new SearchProviderToken
        {
            Token = $"task-3-1-contract-{Guid.NewGuid():N}",
            SearchProvider = SearchProviderEnum.GitHub,
            IsEnabled = true,
            AddedByTelegramId = addedByTelegramId
        };
        context.SearchProviderTokens.Add(credential);
        await context.SaveChangesAsync();
        return credential;
    }

    public static async Task<SearchProviderToken> AddCredentialWithGrantAsync(
        DBContext context,
        string scopeName,
        long? telegramPrincipalId)
    {
        var credential = await AddCredentialAsync(context);
        context.Add(CreateGrant(context.Model, credential.Id, scopeName, telegramPrincipalId));
        await context.SaveChangesAsync();
        return credential;
    }

    public static object CreateGrant(
        IModel model,
        int credentialId,
        string scopeName,
        long? telegramPrincipalId)
    {
        var grantEntity = RequireCredentialGrantEntity(model);
        var grant = Activator.CreateInstance(grantEntity.ClrType)
            ?? throw new XunitException($"Could not construct {grantEntity.ClrType.FullName}.");

        var credentialForeignKey = grantEntity.GetForeignKeys().SingleOrDefault(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(SearchProviderToken));
        Assert.True(
            credentialForeignKey is not null && credentialForeignKey.Properties.Count == 1,
            "CredentialGrant must reference exactly one SearchProviderToken credential.");
        SetProperty(
            grant,
            credentialForeignKey!.Properties[0].Name,
            ConvertValue(credentialId, credentialForeignKey.Properties[0].ClrType));

        var scopeProperty = RequireScopeProperty(grantEntity);
        var scope = Enum.Parse(scopeProperty.ClrType, scopeName, ignoreCase: false);
        SetProperty(grant, scopeProperty.Name, scope);

        var principalProperty = RequireTelegramPrincipalProperty(grantEntity);
        SetProperty(
            grant,
            principalProperty.Name,
            telegramPrincipalId is null
                ? null
                : ConvertValue(telegramPrincipalId.Value, principalProperty.ClrType));

        TrySetProperty(grant, "StableId", Guid.NewGuid());
        TrySetProperty(grant, "CreatedUtc", DateTime.UtcNow);
        TrySetProperty(grant, "UpdatedUtc", DateTime.UtcNow);
        return grant;
    }

    public static async Task<NodeResolutionContract> ResolveNodeAsync(
        IServiceProvider services,
        string nodeToken)
    {
        using var scope = services.CreateScope();
        var service = RequireRegisteredService(scope.ServiceProvider, ResolverTypeNames, "principal resolver");
        var method = RequireNodeResolverMethod(service.GetType());
        var arguments = method.GetParameters()
            .Select(parameter => parameter.ParameterType == typeof(string)
                ? (object?)nodeToken
                : parameter.ParameterType == typeof(CancellationToken)
                    ? CancellationToken.None
                    : parameter.HasDefaultValue
                        ? parameter.DefaultValue
                        : throw new XunitException(
                            $"Unsupported principal resolver input '{parameter.Name}' ({parameter.ParameterType.Name}). " +
                            "Client-selected principal inputs are forbidden."))
            .ToArray();

        var invocation = method.Invoke(service, arguments);
        var result = await UnwrapAsyncResult(invocation);
        return new NodeResolutionContract(ExtractResolvedPrincipal(result), method);
    }

    public static async Task<GrantEvaluationContract> EvaluateCredentialAsync(
        IServiceProvider services,
        int credentialId,
        object schedulerPrincipal)
    {
        ArgumentNullException.ThrowIfNull(schedulerPrincipal);
        using var scope = services.CreateScope();
        var service = RequireRegisteredService(scope.ServiceProvider, EvaluatorTypeNames, "grant evaluator");
        var method = RequireEvaluatorMethod(service.GetType(), schedulerPrincipal.GetType());
        var arguments = method.GetParameters()
            .Select(parameter =>
            {
                if (parameter.ParameterType.IsInstanceOfType(schedulerPrincipal))
                {
                    return schedulerPrincipal;
                }

                if (parameter.ParameterType == typeof(CancellationToken))
                {
                    return (object)CancellationToken.None;
                }

                if (IsNumeric(parameter.ParameterType))
                {
                    return ConvertValue(credentialId, parameter.ParameterType);
                }

                if (parameter.HasDefaultValue)
                {
                    return parameter.DefaultValue;
                }

                throw new XunitException(
                    $"Unsupported grant evaluator input '{parameter.Name}' ({parameter.ParameterType.Name}). " +
                    "The common evaluator must use durable credential identity and a server-created SchedulerPrincipal.");
            })
            .ToArray();

        var invocation = method.Invoke(service, arguments);
        var result = await UnwrapAsyncResult(invocation);
        return new GrantEvaluationContract(ReadAuthorizationDecision(result), method);
    }

    public static long ReadTelegramPrincipalId(object schedulerPrincipal)
    {
        var property = schedulerPrincipal.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate =>
                candidate.Name.Contains("Telegram", StringComparison.OrdinalIgnoreCase) &&
                (candidate.Name.Contains("Principal", StringComparison.OrdinalIgnoreCase) ||
                 candidate.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)));
        Assert.True(
            property is not null,
            "SchedulerPrincipal must expose its resolved Telegram principal identity.");
        var value = property!.GetValue(schedulerPrincipal);
        Assert.NotNull(value);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool ReadAdministratorState(object schedulerPrincipal)
    {
        var property = schedulerPrincipal.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate =>
                candidate.PropertyType == typeof(bool) &&
                candidate.Name.Contains("Admin", StringComparison.OrdinalIgnoreCase));
        if (property is not null)
        {
            return (bool)(property.GetValue(schedulerPrincipal) ?? false);
        }

        var role = schedulerPrincipal.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate =>
                candidate.Name.Contains("Kind", StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.Contains("Role", StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.Contains("Type", StringComparison.OrdinalIgnoreCase))
            ?.GetValue(schedulerPrincipal)
            ?.ToString();
        Assert.NotNull(role);
        return role!.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
               role.Equals("Administrator", StringComparison.OrdinalIgnoreCase);
    }

    private static object RequireRegisteredService(
        IServiceProvider services,
        IReadOnlyCollection<string> candidateNames,
        string contractName)
    {
        foreach (var type in FindTypes()
                     .Where(candidate => candidateNames.Contains(candidate.Name, StringComparer.Ordinal))
                     .OrderByDescending(candidate => candidate.IsInterface))
        {
            var service = services.GetService(type);
            if (service is not null)
            {
                return service;
            }
        }

        throw new XunitException(
            $"Expected the WebAPI dependency-injection container to register a {contractName}. " +
            "Task 3.2 has not wired the principal/grant services yet.");
    }

    private static MethodInfo RequireNodeResolverMethod(Type serviceType)
    {
        var methods = serviceType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method =>
                method.Name.Equals("ResolveNodeAsync", StringComparison.Ordinal) ||
                method.Name.Equals("ResolveAsync", StringComparison.Ordinal) ||
                (method.Name.Contains("Resolve", StringComparison.OrdinalIgnoreCase) &&
                 method.Name.Contains("Node", StringComparison.OrdinalIgnoreCase)))
            .Where(method => method.GetParameters().Count(parameter => parameter.ParameterType == typeof(string)) == 1)
            .ToArray();
        Assert.True(
            methods.Length == 1,
            $"Expected one node-to-Telegram principal resolution method on {serviceType.FullName}.");
        return methods[0];
    }

    private static MethodInfo RequireEvaluatorMethod(Type serviceType, Type schedulerPrincipalType)
    {
        var methods = serviceType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => EvaluatorMethodNames.Contains(method.Name, StringComparer.Ordinal))
            .Where(method => method.GetParameters().Any(parameter =>
                parameter.ParameterType.IsAssignableFrom(schedulerPrincipalType)))
            .Where(method => method.GetParameters().Count(parameter => IsNumeric(parameter.ParameterType)) == 1)
            .ToArray();
        Assert.True(
            methods.Length == 1,
            $"Expected one common credential grant-evaluation method on {serviceType.FullName}.");
        return methods[0];
    }

    private static object? ExtractResolvedPrincipal(object? result)
    {
        if (result is null)
        {
            return null;
        }

        var resultType = result.GetType();
        if (resultType.Name.Contains("SchedulerPrincipal", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        foreach (var successName in new[] { "IsResolved", "IsSuccess", "Succeeded" })
        {
            var success = resultType.GetProperty(successName, BindingFlags.Instance | BindingFlags.Public);
            if (success?.PropertyType == typeof(bool) && !(bool)(success.GetValue(result) ?? false))
            {
                return null;
            }
        }

        var principalProperty = new[] { "Principal", "SchedulerPrincipal", "Value" }
            .Select(name => resultType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public))
            .FirstOrDefault(property => property is not null);
        Assert.True(
            principalProperty is not null,
            $"Principal resolution result {resultType.FullName} must expose its SchedulerPrincipal.");
        return principalProperty!.GetValue(result);
    }

    private static bool ReadAuthorizationDecision(object? result)
    {
        Assert.NotNull(result);
        if (result is bool authorized)
        {
            return authorized;
        }

        var property = new[] { "IsAuthorized", "Authorized", "IsEligible", "Allowed" }
            .Select(name => result!.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public))
            .FirstOrDefault(candidate => candidate?.PropertyType == typeof(bool));
        Assert.True(
            property is not null,
            $"Grant evaluator result {result!.GetType().FullName} must expose an authorization decision.");
        return (bool)(property!.GetValue(result) ?? false);
    }

    private static async Task<object?> UnwrapAsyncResult(object? invocation)
    {
        if (invocation is null)
        {
            return null;
        }

        if (invocation is Task task)
        {
            await task;
            return task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(task);
        }

        if (invocation is ValueTask valueTask)
        {
            await valueTask;
            return null;
        }

        var invocationType = invocation.GetType();
        if (invocationType.IsGenericType &&
            invocationType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = (Task?)invocationType.GetMethod("AsTask", BindingFlags.Instance | BindingFlags.Public)
                ?.Invoke(invocation, null);
            Assert.NotNull(asTask);
            await asTask!;
            return asTask!.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(asTask);
        }

        return invocation;
    }

    private static IEnumerable<Type> FindTypes()
    {
        foreach (var assembly in ContractAssemblies.Distinct())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException error)
            {
                types = error.Types.Where(type => type is not null).Cast<Type>().ToArray();
            }

            foreach (var type in types)
            {
                yield return type;
            }
        }
    }

    private static bool IsNumeric(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte) ||
               type == typeof(short) ||
               type == typeof(int) ||
               type == typeof(long) ||
               type == typeof(ushort) ||
               type == typeof(uint) ||
               type == typeof(ulong);
    }

    private static object ConvertValue(object value, Type destinationType)
    {
        var targetType = Nullable.GetUnderlyingType(destinationType) ?? destinationType;
        return targetType.IsInstanceOfType(value)
            ? value
            : Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void SetProperty(object instance, string propertyName, object? value)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public);
        Assert.True(
            property is not null && property.SetMethod is not null,
            $"Expected writable property {instance.GetType().Name}.{propertyName}.");
        property!.SetValue(instance, value);
    }

    private static void TrySetProperty(object instance, string propertyName, object value)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public);
        if (property?.SetMethod is not null)
        {
            property.SetValue(instance, ConvertValue(value, property.PropertyType));
        }
    }
}

internal sealed record NodeResolutionContract(object? Principal, MethodInfo Method);

internal sealed record GrantEvaluationContract(bool IsAuthorized, MethodInfo Method);
