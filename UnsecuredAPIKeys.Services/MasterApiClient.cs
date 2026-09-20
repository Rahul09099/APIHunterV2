using System.Net.Http.Json;
using UnsecuredAPIKeys.Data.DTOs;

namespace UnsecuredAPIKeys.Services;

/// <summary>
/// Thin Master HTTP surface used by Workers (Wave 13). Implementations perform no
/// scheduling, detection, or credential storage: they transport typed DTOs and surface
/// HTTP outcomes to the cycle runner. No provider tokens are ever read or sent here —
/// only the node token header and operation-scoped claim material.
/// </summary>
public interface IMasterApiClient
{
    Task<NodeSyncDTO?> GetSyncAsync(CancellationToken cancellationToken);

    Task<HttpResponseMessage> ClaimAsync(
        CredentialClaimRequest request, CancellationToken cancellationToken);

    Task<HttpResponseMessage> RenewAsync(
        Guid leaseId, CredentialLeaseRenewRequest request, CancellationToken cancellationToken);

    Task<HttpResponseMessage> CompleteAsync(
        Guid leaseId, CredentialLeaseCompleteRequest request, CancellationToken cancellationToken);

    Task<HttpResponseMessage> ReportAsync(
        NodeBulkReportDto report, CancellationToken cancellationToken);

    Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken);
}

/// <summary>
/// HTTP <see cref="IMasterApiClient"/> bound to one Master origin with the node token
/// header. BaseAddress and authentication are configured once at construction; per-call
/// paths are relative and contain no secrets. Invalid configuration is tolerated here
/// and surfaced as transport failure — <see cref="WorkerScraperOptions.Validate"/>
/// fails closed before any traffic.
/// </summary>
public sealed class HttpMasterApiClient : IMasterApiClient
{
    private readonly HttpClient httpClient;

    public HttpMasterApiClient(HttpClient httpClient, string masterApiUrl, string nodeToken)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        if (Uri.TryCreate(
                (masterApiUrl ?? string.Empty).TrimEnd('/') + "/",
                UriKind.Absolute,
                out var baseAddress))
        {
            this.httpClient.BaseAddress = baseAddress;
        }

        if (!string.IsNullOrWhiteSpace(nodeToken) &&
            !this.httpClient.DefaultRequestHeaders.Contains("X-Node-Token"))
        {
            this.httpClient.DefaultRequestHeaders.Add("X-Node-Token", nodeToken);
        }
    }

    public async Task<NodeSyncDTO?> GetSyncAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("api/v1/nodes/sync", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<NodeSyncDTO>(
            cancellationToken: cancellationToken);
    }

    public Task<HttpResponseMessage> ClaimAsync(
        CredentialClaimRequest request, CancellationToken cancellationToken) =>
        httpClient.PostAsJsonAsync(
            "api/v1/nodes/credential-claims", request, cancellationToken);

    public Task<HttpResponseMessage> RenewAsync(
        Guid leaseId, CredentialLeaseRenewRequest request, CancellationToken cancellationToken) =>
        httpClient.PostAsJsonAsync(
            $"api/v1/nodes/credential-claims/{leaseId:D}/renew", request, cancellationToken);

    public Task<HttpResponseMessage> CompleteAsync(
        Guid leaseId, CredentialLeaseCompleteRequest request, CancellationToken cancellationToken) =>
        httpClient.PostAsJsonAsync(
            $"api/v1/nodes/credential-claims/{leaseId:D}/complete", request, cancellationToken);

    public Task<HttpResponseMessage> ReportAsync(
        NodeBulkReportDto report, CancellationToken cancellationToken) =>
        httpClient.PostAsJsonAsync("api/v1/nodes/report", report, cancellationToken);

    public async Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsync(
                "api/v1/nodes/heartbeat", null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
