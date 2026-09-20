using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
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
/// Expected-red contract tests for multi-search-provider-platform Task 2.1.
/// These tests intentionally use runtime model inspection so the test assembly builds
/// before the additive Provider Instance implementation from Task 2.2 exists.
/// </summary>
public sealed class ProviderInstanceSchemaParityTests
{
    [Fact]
    public void EfModel_DefinesProviderInstanceIdentityLifecycleAndConstraints()
    {
        using var context = ProviderInstanceTestContract.CreateModelContext();
        var designTimeModel =
            ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)context)
            .Instance
            .GetRequiredService<IDesignTimeModel>()
            .Model;
        var entityType = ProviderInstanceTestContract.RequireProviderInstanceEntity(designTimeModel);

        ProviderInstanceTestContract.AssertProperty(entityType, "Id", typeof(long), nullable: false);
        var stableId = ProviderInstanceTestContract.AssertProperty(
            entityType,
            "StableId",
            typeof(Guid),
            nullable: false);
        var providerKind = ProviderInstanceTestContract.AssertProperty(
            entityType,
            "ProviderKind",
            expectedType: null,
            nullable: false);
        Assert.True(providerKind.ClrType.IsEnum, "ProviderKind must be a closed enum.");
        Assert.Contains("GitHub", Enum.GetNames(providerKind.ClrType));
        Assert.Contains("GitLab", Enum.GetNames(providerKind.ClrType));

        ProviderInstanceTestContract.AssertProperty(entityType, "DisplayName", typeof(string), nullable: false);
        ProviderInstanceTestContract.AssertProperty(entityType, "NormalizedScheme", typeof(string), nullable: false);
        ProviderInstanceTestContract.AssertProperty(entityType, "NormalizedHost", typeof(string), nullable: false);
        ProviderInstanceTestContract.AssertProperty(entityType, "NormalizedPort", typeof(int), nullable: false);
        ProviderInstanceTestContract.AssertProperty(entityType, "NormalizedBasePath", typeof(string), nullable: false);
        ProviderInstanceTestContract.AssertProperty(entityType, "IsEnabled", typeof(bool), nullable: false);
        ProviderInstanceTestContract.AssertProperty(entityType, "AllowGlobalPublicSearch", typeof(bool), nullable: false);
        ProviderInstanceTestContract.AssertProperty(entityType, "MaxConcurrentOperations", typeof(int), nullable: false);
        ProviderInstanceTestContract.AssertProperty(entityType, "SettingsVersion", typeof(int), nullable: false);
        ProviderInstanceTestContract.AssertProperty(entityType, "SettingsJson", typeof(string), nullable: false);
        ProviderInstanceTestContract.AssertProperty(entityType, "ApprovedByTelegramId", typeof(long?), nullable: true);
        ProviderInstanceTestContract.AssertProperty(entityType, "CreatedUtc", typeof(DateTime), nullable: false);
        ProviderInstanceTestContract.AssertProperty(entityType, "UpdatedUtc", typeof(DateTime), nullable: false);

        Assert.Equal(PropertySaveBehavior.Throw, stableId.GetAfterSaveBehavior());
        ProviderInstanceTestContract.AssertUniqueIndex(entityType, "StableId");
        ProviderInstanceTestContract.AssertUniqueIndex(
            entityType,
            "ProviderKind",
            "NormalizedScheme",
            "NormalizedHost",
            "NormalizedPort",
            "NormalizedBasePath");

        var concurrencyChecks = entityType.GetCheckConstraints()
            .Select(check => check.Sql)
            .Where(sql => sql is not null)
            .Cast<string>()
            .Where(sql => sql.Contains("MaxConcurrentOperations", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(
            concurrencyChecks.Any(sql => sql.Contains("> 0", StringComparison.Ordinal) ||
                                         sql.Contains(">= 1", StringComparison.Ordinal)),
            "Provider Instance schema must enforce positive MaxConcurrentOperations.");

        var forbiddenSecretColumns = entityType.GetProperties()
            .Select(property => property.Name)
            .Where(name => name.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("NodeToken", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("Token", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("Secret", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(forbiddenSecretColumns);
    }

    [Fact]
    public void EfModel_DefinesSchemaVersionReadinessAndCutoverRecords()
    {
        using var context = ProviderInstanceTestContract.CreateModelContext();
        var persistedNames = context.Model.GetEntityTypes()
            .Select(entityType => $"{entityType.ClrType.Name}|{entityType.GetTableName()}")
            .ToArray();

        ProviderInstanceTestContract.AssertConceptExists(persistedNames, "SchemaVersion");
        ProviderInstanceTestContract.AssertConceptExists(persistedNames, "ReadinessMarker");
        ProviderInstanceTestContract.AssertConceptExists(persistedNames, "CutoverMarker");
    }

    [Fact]
    public void ManualSqliteAndPostgresInitializers_ContainTheSameProviderInstanceContract()
    {
        var source = ProviderInstanceTestContract.ReadRepositoryFile(
            "UnsecuredAPIKeys.Services",
            "DatabaseService.cs");
        var sqlite = ProviderInstanceTestContract.ExtractSection(
            source,
            "private async Task EnsureSQLiteSchemaAsync",
            "private async Task EnsurePostgresSchemaAsync");
        var postgres = ProviderInstanceTestContract.ExtractSection(
            source,
            "private async Task EnsurePostgresSchemaAsync",
            "private async Task SeedDefaultQueriesAsync");

        ProviderInstanceTestContract.AssertSchemaArtifact("manual SQLite initializer", sqlite);
        ProviderInstanceTestContract.AssertSchemaArtifact("manual PostgreSQL initializer", postgres);
    }

    [Fact]
    public void MasterInitSql_ContainsProviderInstanceAndMarkerSchemaParity()
    {
        var sql = ProviderInstanceTestContract.ReadRepositoryFile("master_init.sql");
        ProviderInstanceTestContract.AssertSchemaArtifact("master_init.sql", sql);
    }
}

public sealed class ProviderInstanceStoreContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Store_RejectsNonPositiveConcurrency(int maxConcurrentOperations)
    {
        await using var store = await ProviderInstanceSqliteStore.CreateAsync();
        var entityType = ProviderInstanceTestContract.RequireProviderInstanceEntity(store.Context.Model);
        var instance = ProviderInstanceTestContract.CreateValidProviderInstance(entityType);
        ProviderInstanceTestContract.SetProperty(instance, "MaxConcurrentOperations", maxConcurrentOperations);

        var error = await ProviderInstanceTestContract.CaptureSaveFailureAsync(store.Context, instance);
        ProviderInstanceTestContract.AssertValidationFailure(error, "non-positive concurrency");
    }

    [Theory]
    [InlineData(-99)]
    [InlineData(999)]
    public async Task Store_RejectsUnknownProviderKindsInsteadOfDefaultingToGitHub(int unknownKind)
    {
        await using var store = await ProviderInstanceSqliteStore.CreateAsync();
        var entityType = ProviderInstanceTestContract.RequireProviderInstanceEntity(store.Context.Model);
        var instance = ProviderInstanceTestContract.CreateValidProviderInstance(entityType);
        var providerKindType = entityType.FindProperty("ProviderKind")!.ClrType;
        ProviderInstanceTestContract.SetProperty(
            instance,
            "ProviderKind",
            Enum.ToObject(providerKindType, unknownKind));

        var error = await ProviderInstanceTestContract.CaptureSaveFailureAsync(store.Context, instance);
        ProviderInstanceTestContract.AssertValidationFailure(error, "unknown Provider Kind");
    }

    [Theory]
    [InlineData("{\"credential\":\"ghp_task_2_1_settings_canary\"}", "ghp_task_2_1_settings_canary")]
    [InlineData("{\"nodeToken\":\"task_2_1_node_token_canary\"}", "task_2_1_node_token_canary")]
    public async Task Store_RejectsSecretsInVersionedSettingsWithoutEchoingThem(
        string settingsJson,
        string canary)
    {
        await using var store = await ProviderInstanceSqliteStore.CreateAsync();
        var entityType = ProviderInstanceTestContract.RequireProviderInstanceEntity(store.Context.Model);
        var instance = ProviderInstanceTestContract.CreateValidProviderInstance(entityType);
        ProviderInstanceTestContract.SetProperty(instance, "SettingsVersion", 1);
        ProviderInstanceTestContract.SetProperty(instance, "SettingsJson", settingsJson);

        var error = await ProviderInstanceTestContract.CaptureSaveFailureAsync(store.Context, instance);
        ProviderInstanceTestContract.AssertValidationFailure(error, "secret-bearing settings");
        Assert.DoesNotContain(canary, error!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_EnforcesUniqueNormalizedProviderIdentity()
    {
        await using var store = await ProviderInstanceSqliteStore.CreateAsync();
        var entityType = ProviderInstanceTestContract.RequireProviderInstanceEntity(store.Context.Model);
        var first = ProviderInstanceTestContract.CreateValidProviderInstance(
            entityType,
            stableId: Guid.NewGuid(),
            host: "code.example.test");
        var duplicate = ProviderInstanceTestContract.CreateValidProviderInstance(
            entityType,
            stableId: Guid.NewGuid(),
            host: "code.example.test");

        store.Context.AddRange(first, duplicate);
        var error = await Record.ExceptionAsync(() => store.Context.SaveChangesAsync());

        Assert.IsType<DbUpdateException>(error);
    }

    [Fact]
    public async Task Store_RejectsStableIdMutationAfterInsert()
    {
        await using var store = await ProviderInstanceSqliteStore.CreateAsync();
        var entityType = ProviderInstanceTestContract.RequireProviderInstanceEntity(store.Context.Model);
        var instance = ProviderInstanceTestContract.CreateValidProviderInstance(entityType);
        store.Context.Add(instance);
        await store.Context.SaveChangesAsync();

        ProviderInstanceTestContract.SetProperty(instance, "StableId", Guid.NewGuid());
        var error = await Record.ExceptionAsync(() => store.Context.SaveChangesAsync());

        ProviderInstanceTestContract.AssertValidationFailure(error, "Stable ID mutation");
    }
}

public sealed class ProviderInstanceMigrationAndReadinessTests
{
    [Fact]
    public async Task Initialization_CreatesNormalizedDefaultsAndLinksLegacyCredentialsByKind()
    {
        await using var store = await ProviderInstanceSqliteStore.CreateWithLegacyCredentialsAsync();
        await new DatabaseService(store.Context).InitializeDatabaseAsync();

        var rows = await ProviderInstanceTestContract.ReadProviderInstancesAsync(store.Connection);
        Assert.Equal(5, rows.Count);

        var github = Assert.Single(rows, row => row.ProviderKind == (int)SearchProviderEnum.GitHub);
        Assert.Equal("GitHub", github.DisplayName);
        Assert.Equal("https", github.Scheme);
        Assert.Equal("api.github.com", github.Host);
        Assert.Equal(443, github.Port);
        Assert.Equal("/", github.BasePath);
        ProviderInstanceTestContract.AssertSafeDefault(github);

        var gitlab = Assert.Single(rows, row => row.ProviderKind == (int)SearchProviderEnum.GitLab);
        Assert.Equal("GitLab", gitlab.DisplayName);
        Assert.Equal("https", gitlab.Scheme);
        Assert.Equal("gitlab.com", gitlab.Host);
        Assert.Equal(443, gitlab.Port);
        Assert.Equal("/api/v4", gitlab.BasePath);
        ProviderInstanceTestContract.AssertSafeDefault(gitlab);

        var sourcegraph = Assert.Single(rows, row => row.ProviderKind == (int)SearchProviderEnum.Sourcegraph);
        Assert.Equal("Sourcegraph", sourcegraph.DisplayName);
        Assert.Equal("sourcegraph.com", sourcegraph.Host);
        ProviderInstanceTestContract.AssertSafeDefault(sourcegraph);

        var huggingface = Assert.Single(rows, row => row.ProviderKind == (int)SearchProviderEnum.HuggingFace);
        Assert.Equal("HuggingFace", huggingface.DisplayName);
        Assert.Equal("huggingface.co", huggingface.Host);
        ProviderInstanceTestContract.AssertSafeDefault(huggingface);

        var azure = Assert.Single(rows, row => row.ProviderKind == (int)SearchProviderEnum.AzureDevOps);
        Assert.Equal("AzureDevOps", azure.DisplayName);
        Assert.Equal("dev.azure.com", azure.Host);
        ProviderInstanceTestContract.AssertSafeDefault(azure);

        var links = await ProviderInstanceTestContract.ReadCredentialLinksAsync(store.Connection);
        Assert.Equal(2, links.Count);
        Assert.All(links, link =>
        {
            Assert.NotNull(link.ProviderInstanceId);
            Assert.Equal(link.LegacyProviderKind, link.InstanceProviderKind);
        });
    }

    [Fact]
    public async Task Readiness_IsWithheldWhenLegacyKindConflictsWithLinkedInstance()
    {
        await using var store = await ProviderInstanceSqliteStore.CreateWithLegacyCredentialsAsync();
        await new DatabaseService(store.Context).InitializeDatabaseAsync();

        Assert.True(
            await ProviderInstanceTestContract.EvaluateProviderInstanceReadinessAsync(
                store.Context,
                store.Options),
            "A correctly linked initialized database should be Provider Instance ready.");

        var instances = await ProviderInstanceTestContract.ReadProviderInstancesAsync(store.Connection);
        var gitlabId = Assert.Single(
            instances,
            row => row.ProviderKind == (int)SearchProviderEnum.GitLab).Id;
        var githubTokenId = await store.Context.SearchProviderTokens
            .Where(token => token.SearchProvider == SearchProviderEnum.GitHub)
            .Select(token => token.Id)
            .SingleAsync();

        // Simulate a legacy/out-of-band corruption that predates the runtime write guard.
        // Normal EF writes must reject this mismatch; readiness must still detect it on disk.
        await store.Context.Database.ExecuteSqlRawAsync(
            "DROP TRIGGER IF EXISTS \"FK_SearchProviderTokens_SearchProviderInstances_ProviderInstanceId_Update\"");
        await store.Context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"SearchProviderTokens\" SET \"ProviderInstanceId\" = {gitlabId} WHERE \"Id\" = {githubTokenId}");
        store.Context.ChangeTracker.Clear();

        Assert.False(
            await ProviderInstanceTestContract.EvaluateProviderInstanceReadinessAsync(
                store.Context,
                store.Options));
    }
}

public sealed class ProviderInstanceWebApiStartupTests(SharedMemorySqliteWebApiFixture fixture)
    : IClassFixture<SharedMemorySqliteWebApiFixture>
{
    [Fact]
    public async Task WebApiStartup_CreatesProviderInstanceMarkerSchemaAndPhaseZeroDefaults()
    {
        var tableNames = await ProviderInstanceTestContract.ReadTableNamesAsync(fixture.ConnectionString);
        ProviderInstanceTestContract.AssertConceptExists(tableNames, "ProviderInstance");
        ProviderInstanceTestContract.AssertConceptExists(tableNames, "SchemaVersion");
        ProviderInstanceTestContract.AssertConceptExists(tableNames, "ReadinessMarker");
        ProviderInstanceTestContract.AssertConceptExists(tableNames, "CutoverMarker");

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var defaults = await ProviderInstanceTestContract.ReadProviderInstancesAsync(connection);
        Assert.Contains(defaults, row => row.ProviderKind == (int)SearchProviderEnum.GitHub);
        Assert.Contains(defaults, row => row.ProviderKind == (int)SearchProviderEnum.GitLab);

        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DBContext>();
        Assert.True(await context.SearchProviderInstances.AnyAsync(instance =>
            instance.StableId == ProviderInstanceSchema.DefaultGitHubStableId));
        Assert.True(await context.SearchProviderInstances.AnyAsync(instance =>
            instance.StableId == ProviderInstanceSchema.DefaultGitLabStableId));
    }
}

internal static class ProviderInstanceTestContract
{
    private static readonly string[] SchemaArtifactTerms =
    [
        "SearchProviderInstance",
        "ProviderInstanceId",
        "StableId",
        "ProviderKind",
        "DisplayName",
        "NormalizedScheme",
        "NormalizedHost",
        "NormalizedPort",
        "NormalizedBasePath",
        "IsEnabled",
        "AllowGlobalPublicSearch",
        "MaxConcurrentOperations",
        "SettingsVersion",
        "SettingsJson",
        "ApprovedByTelegramId",
        "CreatedUtc",
        "UpdatedUtc",
        "SchemaVersion",
        "ReadinessMarker",
        "CutoverMarker"
    ];

    public static DBContext CreateModelContext()
    {
        var options = new DbContextOptionsBuilder<DBContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new DBContext(options);
    }

    public static IEntityType RequireProviderInstanceEntity(IModel model)
    {
        var entityType = model.GetEntityTypes().SingleOrDefault(candidate =>
            candidate.ClrType.Name.Equals("SearchProviderInstance", StringComparison.Ordinal) ||
            (candidate.GetTableName()?.Contains("ProviderInstance", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.True(
            entityType is not null,
            "Expected an EF entity/table for SearchProviderInstance. Task 2.2 has not added it yet.");
        return entityType!;
    }

    public static IProperty AssertProperty(
        IEntityType entityType,
        string name,
        Type? expectedType,
        bool nullable)
    {
        var property = entityType.FindProperty(name);
        Assert.True(property is not null, $"Expected {entityType.ClrType.Name}.{name} in the EF model.");
        if (expectedType is not null)
        {
            Assert.Equal(expectedType, property!.ClrType);
        }
        Assert.Equal(nullable, property!.IsNullable);
        return property;
    }

    public static void AssertUniqueIndex(IEntityType entityType, params string[] propertyNames)
    {
        var matchingIndex = entityType.GetIndexes().SingleOrDefault(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
        Assert.True(
            matchingIndex is not null,
            $"Expected index ({string.Join(", ", propertyNames)}) on {entityType.ClrType.Name}.");
        Assert.True(
            matchingIndex!.IsUnique,
            $"Expected index ({string.Join(", ", propertyNames)}) to be unique.");
    }

    public static void AssertConceptExists(IEnumerable<string> values, string concept)
    {
        Assert.Contains(values, value => value.Contains(concept, StringComparison.OrdinalIgnoreCase));
    }

    public static string ReadRepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "UnsecuredAPIKeys-OpenSource.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = relativeSegments.Aggregate(
            directory!.FullName,
            Path.Combine);
        Assert.True(File.Exists(path), $"Expected repository file '{path}'.");
        return File.ReadAllText(path);
    }

    public static string ExtractSection(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, Math.Max(start, 0), StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not locate schema section '{startMarker}'.");
        Assert.True(end > start, $"Could not locate end marker '{endMarker}' after '{startMarker}'.");
        return source[start..end];
    }

    public static void AssertSchemaArtifact(string artifactName, string artifact)
    {
        var missing = SchemaArtifactTerms
            .Where(term => !artifact.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(
            missing.Length == 0,
            $"{artifactName} is missing Provider Instance schema terms: {string.Join(", ", missing)}");
    }

    public static object CreateValidProviderInstance(
        IEntityType entityType,
        Guid? stableId = null,
        string host = "provider-instance-contract.example.test")
    {
        var instance = Activator.CreateInstance(entityType.ClrType)
            ?? throw new XunitException($"Could not construct {entityType.ClrType.FullName}.");
        SetProperty(instance, "StableId", stableId ?? Guid.NewGuid());
        var providerKindType = entityType.FindProperty("ProviderKind")?.ClrType
            ?? throw new XunitException("ProviderKind is missing from SearchProviderInstance.");
        SetProperty(instance, "ProviderKind", Enum.ToObject(providerKindType, (int)SearchProviderEnum.GitHub));
        SetProperty(instance, "DisplayName", "Contract Test Provider");
        SetProperty(instance, "NormalizedScheme", "https");
        SetProperty(instance, "NormalizedHost", host);
        SetProperty(instance, "NormalizedPort", 443);
        SetProperty(instance, "NormalizedBasePath", "/api/v1");
        SetProperty(instance, "IsEnabled", true);
        SetProperty(instance, "AllowGlobalPublicSearch", false);
        SetProperty(instance, "MaxConcurrentOperations", 2);
        SetProperty(instance, "SettingsVersion", 1);
        SetProperty(instance, "SettingsJson", "{}");
        SetProperty(instance, "ApprovedByTelegramId", null);
        SetProperty(instance, "CreatedUtc", DateTime.UtcNow);
        SetProperty(instance, "UpdatedUtc", DateTime.UtcNow);
        return instance;
    }

    public static void SetProperty(object instance, string propertyName, object? value)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.True(property is not null, $"Expected public property {instance.GetType().Name}.{propertyName}.");
        property!.SetValue(instance, value);
    }

    public static async Task<Exception?> CaptureSaveFailureAsync(DBContext context, object instance)
    {
        context.Add(instance);
        return await Record.ExceptionAsync(() => context.SaveChangesAsync());
    }

    public static void AssertValidationFailure(Exception? error, string scenario)
    {
        Assert.True(error is not null, $"Expected persistence to reject {scenario}.");
        Assert.True(
            error is DbUpdateException or InvalidOperationException or ArgumentException or ValidationException,
            $"Expected a validation/persistence failure for {scenario}, but received {error!.GetType().FullName}.");
    }

    public static async Task<List<ProviderInstanceRow>> ReadProviderInstancesAsync(SqliteConnection connection)
    {
        var tableName = await FindTableNameAsync(connection, "ProviderInstance");
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT "Id", "StableId", "ProviderKind", "DisplayName",
                   "NormalizedScheme", "NormalizedHost", "NormalizedPort", "NormalizedBasePath",
                   "IsEnabled", "AllowGlobalPublicSearch", "MaxConcurrentOperations",
                   "SettingsVersion", "SettingsJson", "ApprovedByTelegramId", "CreatedUtc", "UpdatedUtc"
            FROM {QuoteIdentifier(tableName)}
            ORDER BY "ProviderKind";
            """;

        var rows = new List<ProviderInstanceRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ProviderInstanceRow(
                Convert.ToInt64(reader.GetValue(0)),
                Convert.ToString(reader.GetValue(1)) ?? string.Empty,
                Convert.ToInt32(reader.GetValue(2)),
                Convert.ToString(reader.GetValue(3)) ?? string.Empty,
                Convert.ToString(reader.GetValue(4)) ?? string.Empty,
                Convert.ToString(reader.GetValue(5)) ?? string.Empty,
                Convert.ToInt32(reader.GetValue(6)),
                Convert.ToString(reader.GetValue(7)) ?? string.Empty,
                Convert.ToBoolean(reader.GetValue(8)),
                Convert.ToBoolean(reader.GetValue(9)),
                Convert.ToInt32(reader.GetValue(10)),
                Convert.ToInt32(reader.GetValue(11)),
                Convert.ToString(reader.GetValue(12)) ?? string.Empty,
                reader.IsDBNull(13) ? null : Convert.ToInt64(reader.GetValue(13)),
                Convert.ToString(reader.GetValue(14)) ?? string.Empty,
                Convert.ToString(reader.GetValue(15)) ?? string.Empty));
        }

        return rows;
    }

    public static async Task<List<CredentialLinkRow>> ReadCredentialLinksAsync(SqliteConnection connection)
    {
        var providerTable = await FindTableNameAsync(connection, "ProviderInstance");
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT token."SearchProvider", token."ProviderInstanceId", instance."ProviderKind"
            FROM "SearchProviderTokens" AS token
            LEFT JOIN {QuoteIdentifier(providerTable)} AS instance
              ON instance."Id" = token."ProviderInstanceId"
            ORDER BY token."SearchProvider";
            """;

        var rows = new List<CredentialLinkRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new CredentialLinkRow(
                Convert.ToInt32(reader.GetValue(0)),
                reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1)),
                reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2))));
        }

        return rows;
    }

    public static void AssertSafeDefault(ProviderInstanceRow row)
    {
        Assert.True(Guid.TryParse(row.StableId, out var stableId));
        Assert.NotEqual(Guid.Empty, stableId);
        Assert.True(row.IsEnabled);
        Assert.False(row.AllowGlobalPublicSearch);
        Assert.True(row.MaxConcurrentOperations > 0);
        Assert.True(row.SettingsVersion > 0);
        Assert.False(string.IsNullOrWhiteSpace(row.SettingsJson));
        Assert.DoesNotContain("credential", row.SettingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nodeToken", row.SettingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", row.SettingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Null(row.ApprovedByTelegramId);
        Assert.True(DateTimeOffset.TryParse(row.CreatedUtc, out _));
        Assert.True(DateTimeOffset.TryParse(row.UpdatedUtc, out _));
    }

    public static async Task<HashSet<string>> ReadTableNamesAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    public static object ConvertValue(object value, Type destinationType)
    {
        var underlyingType = Nullable.GetUnderlyingType(destinationType) ?? destinationType;
        return Convert.ChangeType(value, underlyingType);
    }

    public static async Task<bool> EvaluateProviderInstanceReadinessAsync(
        DBContext context,
        DbContextOptions<DBContext> options)
    {
        var assemblies = new[]
        {
            typeof(DBContext).Assembly,
            typeof(DatabaseService).Assembly,
            typeof(global::Program).Assembly
        };
        var candidates = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => type.Name.Contains("Readiness", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(type => type.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            candidates.Length > 0,
            "Expected a registered/constructible readiness service for Provider Instance compatibility validation.");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(context);
        services.AddSingleton<DBContext>(context);
        services.AddSingleton<IDbContextFactory<DBContext>>(new TestDbContextFactory(options));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();

        var diagnostics = new List<string>();
        foreach (var candidate in candidates)
        {
            object service;
            try
            {
                service = ActivatorUtilities.CreateInstance(provider, candidate);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"{candidate.Name}: construction failed ({ex.GetType().Name})");
                continue;
            }

            var methods = candidate.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name.Contains("Ready", StringComparison.OrdinalIgnoreCase) ||
                                 method.Name.Contains("Validate", StringComparison.OrdinalIgnoreCase) ||
                                 method.Name.Contains("Evaluate", StringComparison.OrdinalIgnoreCase) ||
                                 method.Name.Contains("Check", StringComparison.OrdinalIgnoreCase))
                .Where(method => method.GetParameters().All(parameter =>
                    parameter.ParameterType == typeof(CancellationToken)))
                .OrderByDescending(method => method.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var method in methods)
            {
                try
                {
                    var arguments = method.GetParameters()
                        .Select(_ => (object)CancellationToken.None)
                        .ToArray();
                    var invocationResult = method.Invoke(service, arguments);
                    var result = await UnwrapAsyncResult(invocationResult);
                    if (TryReadReadyState(result, out var isReady))
                    {
                        return isReady;
                    }
                    diagnostics.Add($"{candidate.Name}.{method.Name}: no readable readiness state");
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"{candidate.Name}.{method.Name}: invocation failed ({ex.GetBaseException().GetType().Name})");
                }
            }
        }

        throw new XunitException(
            "No readiness service exposed a Provider Instance readiness state. " +
            string.Join("; ", diagnostics));
    }

    private static async Task<object?> UnwrapAsyncResult(object? value)
    {
        if (value is Task task)
        {
            await task;
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        if (value is not null &&
            value.GetType().IsValueType &&
            value.GetType().FullName?.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal) == true)
        {
            var asTask = value.GetType().GetMethod("AsTask", BindingFlags.Instance | BindingFlags.Public);
            var valueTaskAsTask = (Task?)asTask?.Invoke(value, null);
            if (valueTaskAsTask is not null)
            {
                await valueTaskAsTask;
                return valueTaskAsTask.GetType().GetProperty("Result")?.GetValue(valueTaskAsTask);
            }
        }

        return value;
    }

    private static bool TryReadReadyState(object? result, out bool isReady)
    {
        if (result is bool boolean)
        {
            isReady = boolean;
            return true;
        }

        if (result is null)
        {
            isReady = false;
            return false;
        }

        var preferredProperties = new[]
        {
            "ProviderInstancesReady",
            "ProviderInstanceReady",
            "IsProviderInstanceReady",
            "IsReady",
            "Ready",
            "IsHealthy",
            "Healthy"
        };
        foreach (var propertyName in preferredProperties)
        {
            var property = result.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property?.PropertyType == typeof(bool))
            {
                isReady = (bool)property.GetValue(result)!;
                return true;
            }
        }

        var statusProperty = result.GetType().GetProperty("Status", BindingFlags.Instance | BindingFlags.Public);
        if (statusProperty is not null)
        {
            var status = Convert.ToString(statusProperty.GetValue(result));
            if (status is not null)
            {
                isReady = status.Equals("Healthy", StringComparison.OrdinalIgnoreCase) ||
                          status.Equals("Ready", StringComparison.OrdinalIgnoreCase);
                return true;
            }
        }

        isReady = false;
        return false;
    }

    private static async Task<string> FindTableNameAsync(SqliteConnection connection, string concept)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE $pattern;";
        command.Parameters.AddWithValue("$pattern", $"%{concept}%");
        var value = await command.ExecuteScalarAsync();
        return value as string
            ?? throw new XunitException($"Expected a SQLite table containing '{concept}'.");
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    private sealed class TestDbContextFactory(DbContextOptions<DBContext> options)
        : IDbContextFactory<DBContext>
    {
        public DBContext CreateDbContext() => new(options);

        public Task<DBContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

internal sealed class ProviderInstanceSqliteStore : IAsyncDisposable
{
    private ProviderInstanceSqliteStore(
        SqliteConnection connection,
        DbContextOptions<DBContext> options,
        DBContext context)
    {
        Connection = connection;
        Options = options;
        Context = context;
    }

    public SqliteConnection Connection { get; }
    public DbContextOptions<DBContext> Options { get; }
    public DBContext Context { get; }

    public static async Task<ProviderInstanceSqliteStore> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DBContext>()
            .UseSqlite(connection)
            .Options;
        var context = new DBContext(options);
        await context.Database.EnsureCreatedAsync();
        return new ProviderInstanceSqliteStore(connection, options, context);
    }

    public static async Task<ProviderInstanceSqliteStore> CreateWithLegacyCredentialsAsync()
    {
        var store = await CreateAsync();
        store.Context.SearchProviderTokens.AddRange(
            new SearchProviderToken
            {
                Token = "ghp_task_2_1_legacy_github",
                SearchProvider = SearchProviderEnum.GitHub,
                IsEnabled = true
            },
            new SearchProviderToken
            {
                Token = "glpat-task-2-1-legacy-gitlab",
                SearchProvider = SearchProviderEnum.GitLab,
                IsEnabled = true
            });
        await store.Context.SaveChangesAsync();
        return store;
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

internal sealed record ProviderInstanceRow(
    long Id,
    string StableId,
    int ProviderKind,
    string DisplayName,
    string Scheme,
    string Host,
    int Port,
    string BasePath,
    bool IsEnabled,
    bool AllowGlobalPublicSearch,
    int MaxConcurrentOperations,
    int SettingsVersion,
    string SettingsJson,
    long? ApprovedByTelegramId,
    string CreatedUtc,
    string UpdatedUtc);

internal sealed record CredentialLinkRow(
    int LegacyProviderKind,
    long? ProviderInstanceId,
    int? InstanceProviderKind);
