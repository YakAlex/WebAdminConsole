using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Authentication;

namespace AdminConsole.Api.Security;

/// <summary>
/// Maps AD group membership (Authorization:ViewerGroup — "AdminConsole-Admins")
/// into ClaimTypes.Role. Under IIS, `WindowsPrincipal` had roles = AD groups
/// out of the box; under Kestrel + Negotiate outside IIS, group membership is
/// NOT mapped into claims automatically (R4) — without this class,
/// authorization would either let everyone through or let no one through.
///
/// Called on every authenticated request (the framework doesn't cache
/// IClaimsTransformation between requests); guarded against re-transforming
/// the same principal by checking for the already-added claim up front.
///
/// Windows-only by design (WindowsIdentity, System.DirectoryServices.AccountManagement) —
/// consistent with the rest of the app (Windows Service, DPAPI-NG, future WMI/quser in Phase 4).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGroupClaimsTransformation : IClaimsTransformation
{
    private readonly string _requiredGroup;
    private readonly ILogger<WindowsGroupClaimsTransformation> _logger;

    public WindowsGroupClaimsTransformation(
        IConfiguration config, ILogger<WindowsGroupClaimsTransformation> logger)
    {
        _requiredGroup = config["Authorization:ViewerGroup"]
            ?? throw new InvalidOperationException("Authorization:ViewerGroup is not configured.");
        _logger = logger;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not WindowsIdentity identity || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        if (principal.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == _requiredGroup))
            return Task.FromResult(principal); // already transformed for this request

        bool isMember;
        try
        {
            using var context = new PrincipalContext(ContextType.Domain);
            using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, identity.Name);
            isMember = user is not null && user.IsMemberOf(context, IdentityType.SamAccountName, _requiredGroup);
        }
        catch (Exception ex)
        {
            // Machine off-domain / domain controller unreachable — treated as
            // "cannot confirm membership" (resulting in a 403 further down the
            // pipeline), NOT a 500. This is both a legitimate production risk
            // (R1 — the service runs under a local rather than gMSA account)
            // and an expected scenario for local testing outside AD (a
            // developer's non-domain-joined machine).
            _logger.LogWarning(ex,
                "WindowsGroupClaimsTransformation: failed to verify {User}'s membership in AD group {Group} " +
                "— the domain is unreachable or the machine is not domain-joined.",
                identity.Name, _requiredGroup);
            return Task.FromResult(principal);
        }

        if (!isMember)
            return Task.FromResult(principal);

        // WindowsIdentity.RoleClaimType defaults to ClaimTypes.GroupSid, NOT
        // ClaimTypes.Role — a property that Clone() inherits and that cannot
        // be changed after the fact. So AddClaim(ClaimTypes.Role, ...) directly
        // on a cloned WindowsIdentity does NOT work: User.IsInRole(...)/
        // RequireRole(...) only look for claims matching identity.RoleClaimType,
        // which stays GroupSid no matter what claims you add to it (verified
        // empirically: the claim is visible in User.Claims, yet IsInRole still
        // returns false).
        //
        // The fix — a separate, plain ClaimsIdentity with an explicit
        // RoleClaimType = ClaimTypes.Role. ClaimsPrincipal.IsInRole() checks
        // ALL ClaimsIdentity instances on the principal, so it's enough to add
        // this second identity alongside the original WindowsIdentity.
        var roleIdentity = new ClaimsIdentity(
            claims: [new Claim(ClaimTypes.Role, _requiredGroup)],
            authenticationType: null,
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);

        var clone = principal.Clone();
        clone.AddIdentity(roleIdentity);
        return Task.FromResult(clone);
    }
}
