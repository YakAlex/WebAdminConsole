using System.Security.Claims;
using AdminConsole.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AdminConsole.Api.Controllers;

/// <summary>
/// Replaces the native browser Negotiate popup with a custom login form.
///
/// AD group membership (Authorization:ViewerGroup) is checked ONCE here, at
/// login, and baked into the auth cookie as a Role claim — it is NOT
/// re-verified against AD on every subsequent request (see
/// docs/superpowers/plans/2026-08-26-custom-ad-login.md, Global
/// Constraints, for why: this removes all per-request AD load, at the
/// deliberately accepted cost that a user removed from AdminConsole-Admins
/// keeps access until their cookie expires (8h sliding, Program.cs) or they
/// log out).
///
/// Authentication itself still only proves "valid AD account" — a valid AD
/// user outside the group can log in but gets no Role claim, so the
/// "Viewer" policy (RequireRole) still blocks them on every API call,
/// exactly as it did under Negotiate.
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthController(
    IAdAuthenticationService adAuth, IConfiguration configuration, ILogger<AuthController> logger)
    : ControllerBase
{
    private readonly string _requiredGroup = configuration["Authorization:ViewerGroup"]
        ?? throw new InvalidOperationException("Authorization:ViewerGroup is not configured.");

    public sealed record LoginRequest(string Username, string Password);

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "Ім'я користувача та пароль обов'язкові." });

        if (!adAuth.ValidateCredentials(request.Username, request.Password, out var canonicalUsername))
        {
            // Deliberately the same message for "no such user" and "wrong
            // password" — never reveal which. Never logs the password.
            logger.LogWarning("Login failed for {User} from {RemoteIp}.",
                request.Username, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Невірне ім'я користувача або пароль." });
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, canonicalUsername) };

        bool isMember;
        try
        {
            isMember = adAuth.IsMemberOfViewerGroup(canonicalUsername, _requiredGroup);
        }
        catch (Exception ex)
        {
            // Domain controller unreachable at the moment of login — fail
            // closed (treated as "not a member"), NOT as a login failure:
            // the credentials themselves were valid.
            logger.LogWarning(ex, "Failed to verify {User}'s AD group membership at login.", canonicalUsername);
            isMember = false;
        }

        if (isMember)
            claims.Add(new Claim(ClaimTypes.Role, _requiredGroup));

        var identity = new ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            // IsPersistent = true is intentional, not an oversight: it makes
            // the cookie survive a browser restart (still bounded by the 8h
            // sliding ExpireTimeSpan in Program.cs) rather than being a
            // session-only cookie the browser discards on close. Do not
            // "fix" this into a security regression or treat it as an
            // unexplained mystery — it's a deliberate UX choice from the
            // original plan.
            new AuthenticationProperties { IsPersistent = true });

        logger.LogInformation("Login succeeded for {User} from {RemoteIp} (AdminConsole-Admins member: {IsMember}).",
            canonicalUsername, HttpContext.Connection.RemoteIpAddress, isMember);
        // 204, not 200: an empty-body 200 makes the frontend's apiSend()
        // (adminconsole-web/src/lib/api/http.ts) call response.json() on an
        // empty body, which throws and gets rethrown as ApiError — so a
        // successful login would render as "wrong username or password"
        // even though the cookie was set correctly. Matches the NoContent()
        // pattern used by every other empty-success response in this API
        // (CredentialsController, DowntimeController, TelegramUsersController).
        return NoContent();
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }
}
