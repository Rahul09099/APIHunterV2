using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace UnsecuredAPIKeys.WebAPI.Services;

public enum DashboardAccessRole
{
    User,
    Admin
}

public sealed record DashboardAccessSession(
    string AccessToken,
    DashboardAccessRole Role,
    DateTime ExpiresAtUtc);

/// <summary>
/// Provides short-lived dashboard sessions for the access codes configured by the host.
/// Codes are read only from environment variables and are never persisted.
/// </summary>
public sealed class DashboardAccessService
{
    private const string UserAccessCodeVariable = "USER_ACCESS_CODE";
    private const string AdminAccessCodeVariable = "ADMIN_ACCESS_CODE";
    private readonly ConcurrentDictionary<string, DashboardAccessSession> _sessions = new();

    public DashboardAccessSession? CreateSession(string? accessCode)
    {
        if (string.IsNullOrWhiteSpace(accessCode)) return null;

        var role = ResolveRole(accessCode);
        if (role is null) return null;

        RemoveExpiredSessions();

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var session = new DashboardAccessSession(
            token,
            role.Value,
            DateTime.UtcNow.AddHours(GetSessionDurationHours()));

        _sessions[token] = session;
        return session;
    }

    public bool TryGetSession(string? accessToken, out DashboardAccessSession? session)
    {
        session = null;
        if (string.IsNullOrWhiteSpace(accessToken)) return false;

        if (!_sessions.TryGetValue(accessToken, out var candidate)) return false;
        if (candidate.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _sessions.TryRemove(accessToken, out _);
            return false;
        }

        session = candidate;
        return true;
    }

    public void RevokeSession(string? accessToken)
    {
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            _sessions.TryRemove(accessToken, out _);
        }
    }

    private static DashboardAccessRole? ResolveRole(string accessCode)
    {
        // Check administrator code first so an identical user code cannot downgrade access.
        if (Matches(Environment.GetEnvironmentVariable(AdminAccessCodeVariable), accessCode))
        {
            return DashboardAccessRole.Admin;
        }

        return Matches(Environment.GetEnvironmentVariable(UserAccessCodeVariable), accessCode)
            ? DashboardAccessRole.User
            : null;
    }

    private static bool Matches(string? configuredCode, string suppliedCode)
    {
        if (string.IsNullOrEmpty(configuredCode)) return false;

        var configuredBytes = Encoding.UTF8.GetBytes(configuredCode);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedCode);
        return configuredBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(configuredBytes, suppliedBytes);
    }

    private static double GetSessionDurationHours()
    {
        const double defaultHours = 12;
        var configured = Environment.GetEnvironmentVariable("ACCESS_SESSION_HOURS");
        return double.TryParse(configured, out var hours) && hours is > 0 and <= 24
            ? hours
            : defaultHours;
    }

    private void RemoveExpiredSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var session in _sessions.Where(pair => pair.Value.ExpiresAtUtc <= now))
        {
            _sessions.TryRemove(session.Key, out _);
        }
    }
}
