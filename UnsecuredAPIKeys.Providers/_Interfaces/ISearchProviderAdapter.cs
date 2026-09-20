using System.Runtime.CompilerServices;
using UnsecuredAPIKeys.Data.Common;

namespace UnsecuredAPIKeys.Providers._Interfaces;

[Flags]
public enum SearchProviderCapability
{
    None = 0,
    CodeSearch = 1 << 0,
    PaginatedSearch = 1 << 1,
    StreamingSearch = 1 << 2,
    ContentRetrieval = 1 << 3,
    PrivateRepositories = 1 << 4,
    GlobalPublicSearch = 1 << 5,
    SelfHosted = 1 << 6,
    RepositoryMetadata = 1 << 7,
    NativeQueryOverrides = 1 << 8
}

public enum ProviderOutcomeKind
{
    Success,
    RateLimited,
    AuthInvalid,
    ForbiddenScope,
    Transient,
    RequestInvalid,
    ResourceMissing,
    Cancellation
}

/// <summary>
/// Provider error classifier interface (AC-8.20).
/// Classifies HTTP status codes and exceptions consistently across search and content retrieval.
/// </summary>
public interface ISearchProviderOutcomeClassifier
{
    ProviderOutcomeKind Classify(
        SearchProviderEnum providerKind,
        System.Net.HttpStatusCode? statusCode,
        Exception? exception = null,
        string? responseBody = null);
}

public sealed record ProviderSettingDefinition(
    string Name,
    string ValueType,
    bool IsRequired,
    string Description);

public sealed record ProviderSettingsSchema(
    int Version,
    IReadOnlyList<ProviderSettingDefinition> Settings)
{
    public static ProviderSettingsSchema Empty { get; } = new(1, []);
}

public sealed record ValidatedProviderInstance(
    Guid StableId,
    SearchProviderEnum ProviderKind,
    string Scheme,
    string Host,
    int Port,
    string BasePath,
    int SettingsVersion,
    string SettingsJson);

public sealed record ProviderOperationBounds(
    long MaxResponseSizeBytes,
    IReadOnlySet<string> AllowedContentTypes,
    int MaxEventCount,
    int MaxContentConcurrency,
    TimeSpan Timeout);

public sealed record ProviderOperationContext(
    ValidatedProviderInstance ProviderInstance,
    ProviderOperationBounds Bounds,
    string? Continuation,
    string? ContinuationAdapterVersion,
    /// <summary>
    /// Active Operation Slot ID. Must be non-null for any credentialed runtime invocation.
    /// Null contexts are rejected fail-closed.
    /// </summary>
    Guid? SlotId = null,
    /// <summary>
    /// Active Lease ID for credentialed operations (ValidateCredential, Search, FetchContent).
    /// Null for unauthenticated operations (CapabilityDiscovery with approved public instance).
    /// </summary>
    Guid? LeaseId = null,
    CredentialMaterial? Credential = null,
    string? SearchQuery = null,
    string? ContentPath = null,
    string? ContentRevision = null,
    string? ContentRepositoryOwner = null,
    string? ContentRepositoryName = null,
    string? ContentApiUrl = null);

public sealed record CredentialMaterial(string Value);

public sealed record CredentialValidationResult(bool IsValid);

public sealed record SearchQuerySnapshot(string GenericQuery, string? NativeOverride, string SettingsJson);

public sealed record TranslatedProviderQuery(string Query, string SettingsJson);

public sealed record NormalizedSearchPage(IReadOnlyList<object> Results);

public sealed record NormalizedContent(string Content, string ContentVersion);

public sealed record ProviderCapabilityResult(
    ProviderOutcomeKind Outcome,
    SearchProviderCapability DiscoveredCapabilities,
    string? SanitizedCode = null);

public sealed record ProviderOperationResult<TValue, TOutcome>(
    TOutcome Outcome,
    TValue? Value,
    string? Continuation = null,
    string? SanitizedCode = null);

/// <summary>
/// Versioned provider contract. Task 6.2 exposes metadata only; runtime methods remain
/// fail-closed until the Claim and Operation Slot guarded runtime is introduced.
/// </summary>
public interface ISearchProviderAdapter
{
    SearchProviderEnum ProviderKind { get; }

    string AdapterVersion { get; }

    SearchProviderCapability DeclaredCapabilities { get; }

    ProviderSettingsSchema SettingsSchema { get; }

    Task<ProviderCapabilityResult> DiscoverCapabilitiesAsync(
        ValidatedProviderInstance instance,
        CancellationToken cancellationToken);

    Task<ProviderOperationResult<CredentialValidationResult, ProviderOutcomeKind>> ValidateCredentialAsync(
        ProviderOperationContext context,
        CredentialMaterial credential,
        CancellationToken cancellationToken);

    Task<TranslatedProviderQuery> TranslateQueryAsync(
        ProviderOperationContext context,
        SearchQuerySnapshot query,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ProviderOperationResult<NormalizedSearchPage, ProviderOutcomeKind>> SearchAsync(
        ProviderOperationContext context,
        CancellationToken cancellationToken);

    Task<ProviderOperationResult<NormalizedContent, ProviderOutcomeKind>> FetchContentAsync(
        ProviderOperationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Guards adapter runtime invocations. Requires an active Operation Slot to be
/// present in the context. Credentialed operations (SearchAsync, ValidateCredentialAsync,
/// FetchContentAsync) additionally require an active Lease ID.
/// Throws fail-closed when either required field is missing.
/// </summary>
internal static class ValidatedOperationRuntime
{
    private const string MissingSlotMessage =
        "Provider adapter runtime invocation is disabled until an Operation Slot and, when required, a credential Claim are active.";

    private const string MissingLeaseMessage =
        "This credentialed adapter operation requires an active Lease. Acquire a Claim before invoking credentialed methods.";

    /// <summary>
    /// Guards invocations that require only a Slot (capability discovery with approved instance).
    /// </summary>
    public static void RequireSlot(ProviderOperationContext? context)
    {
        if (context?.SlotId is null)
            throw new InvalidOperationException(MissingSlotMessage);
    }

    /// <summary>
    /// Guards credentialed invocations that require both Slot and Lease.
    /// </summary>
    public static void RequireSlotAndLease(ProviderOperationContext? context)
    {
        if (context?.SlotId is null)
            throw new InvalidOperationException(MissingSlotMessage);
        if (context.LeaseId is null)
            throw new InvalidOperationException(MissingLeaseMessage);
    }

    public static Task<T> RejectMissingSlotAsync<T>() =>
        Task.FromException<T>(new InvalidOperationException(MissingSlotMessage));

    public static async IAsyncEnumerable<T> RejectMissingSlotStreamAsync<T>(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(MissingSlotMessage);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

/// <summary>Kept for backward compatibility with legacy adapter stubs that have not yet been updated.
/// New adapters must use <see cref="ValidatedOperationRuntime"/>.</summary>
internal static class DisabledAdapterRuntime
{
    private const string Message =
        "Provider adapter runtime invocation is disabled until an Operation Slot and, when required, a credential Claim are active.";

    public static Task<T> RejectAsync<T>() => Task.FromException<T>(new InvalidOperationException(Message));

    public static async IAsyncEnumerable<T> RejectStreamAsync<T>(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(Message);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
