namespace UnsecuredAPIKeys.Data.DTOs;

/// <summary>
/// Worker credential Claim request. The Scheduler Principal is never taken from this
/// body: the node authenticates with X-Node-Token and the Master resolves the principal
/// server-side (AC-1.6). No credential material is ever sent in this direction.
/// When <see cref="WorkItemStableId"/> is empty, the Master creates durable Work
/// server-side from the supplied query snapshot (Worker flow); otherwise the
/// referenced Work Item must exist.
/// </summary>
public sealed record CredentialClaimRequest(
    Guid ProviderInstanceStableId,
    Guid WorkItemStableId,
    string PartitionKey,
    Guid RequestId,
    string GenericQuery = "",
    string? NativeOverride = null,
    string SettingsJson = "{}");

/// <summary>
/// Worker credential Claim response. <see cref="CredentialMaterial"/> carries the
/// one operation-scoped secret and is present only on the initial Claim: it is never
/// stored server-side, never logged, and never re-issued on active or terminal replay
/// (one-secret/no-store). <see cref="OperationSlotId"/> is the non-secret Operation Slot
/// the worker must present for every adapter invocation. All fields are stable
/// references safe for diagnostics.
/// </summary>
public sealed record CredentialClaimResponse(
    Guid LeaseId,
    Guid CredentialStableId,
    Guid ProviderInstanceStableId,
    DateTime LeaseExpiresUtc,
    long CredentialRevision,
    double RenewalThresholdSeconds,
    string? CredentialMaterial,
    bool Replayed,
    Guid OperationSlotId = default,
    Guid? WorkItemStableId = null,
    string? PartitionKey = null);

/// <summary>
/// Lease renewal request. Completes the six-field renewal contract (AC-5.38 + design §10):
/// credential Stable ID, expected Revision, Work Item ID, requested extension, active
/// Lease ID (URL path), and operation identity (Provider Instance Stable ID + Partition key).
/// Non-secret: carries references only, never credential material.
/// </summary>
public sealed record CredentialLeaseRenewRequest(
    Guid CredentialStableId,
    long ExpectedRevision,
    Guid WorkItemStableId,
    Guid ProviderInstanceStableId,
    string PartitionKey,
    double RequestedExtensionSeconds);

/// <summary>
/// Lease renewal response. Non-secret. <see cref="CurrentRevision"/> is the credential
/// revision after renewal — workers must present it on the next renew or complete.
/// </summary>
public sealed record CredentialLeaseRenewResponse(
    bool Succeeded,
    DateTime? NewLeaseExpiresUtc,
    string? FailureReason,
    long? CurrentRevision = null);

/// <summary>
/// Lease completion request. The outcome is the typed provider-outcome name
/// (e.g. Success, Transient, AuthInvalid). Identity fields are optional: when supplied
/// they must match the durable Claim or the completion is rejected as a conflict.
/// Non-secret.
/// </summary>
public sealed record CredentialLeaseCompleteRequest(
    Guid CredentialStableId,
    long ExpectedRevision,
    string Outcome,
    Guid? WorkItemStableId = null,
    Guid? ProviderInstanceStableId = null,
    string? PartitionKey = null);

/// <summary>Lease completion response. Non-secret.</summary>
public sealed record CredentialLeaseCompleteResponse(
    bool Succeeded,
    string? FailureReason);

/// <summary>Typed conflict response. Carries retry guidance, never secrets.</summary>
public sealed record CredentialClaimConflict(
    string Message,
    double? RetryAfterSeconds = null);
