using Microsoft.AspNetCore.Mvc;
using UnsecuredAPIKeys.WebAPI.Services;

namespace UnsecuredAPIKeys.WebAPI.Controllers;

[ApiController]
[Route("api/access")]
public class AccessController : ControllerBase
{
    private readonly DashboardAccessService _accessService;

    public AccessController(DashboardAccessService accessService)
    {
        _accessService = accessService;
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] AccessCodeRequest request)
    {
        var session = _accessService.CreateSession(request.AccessCode);
        if (session is null)
        {
            return Unauthorized(new { message = "Invalid access code" });
        }

        return Ok(new
        {
            accessToken = session.AccessToken,
            role = session.Role.ToString(),
            expiresAtUtc = session.ExpiresAtUtc
        });
    }

    [HttpGet("me")]
    public IActionResult Me([FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        if (!_accessService.TryGetSession(accessToken, out var session) || session is null)
        {
            return Unauthorized(new { message = "Session expired or invalid" });
        }

        return Ok(new { role = session.Role.ToString(), expiresAtUtc = session.ExpiresAtUtc });
    }

    [HttpPost("logout")]
    public IActionResult Logout([FromHeader(Name = "X-Access-Token")] string? accessToken)
    {
        _accessService.RevokeSession(accessToken);
        return NoContent();
    }
}

public class AccessCodeRequest
{
    public string AccessCode { get; set; } = string.Empty;
}
