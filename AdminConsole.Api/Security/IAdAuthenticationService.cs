namespace AdminConsole.Api.Security;

/// <summary>
/// AD-facing credential/group operations. Both methods are called exactly
/// once, at login (AuthController) — group membership is deliberately NOT
/// re-checked on every request (see the plan's Global Constraints on
/// revocation lag vs. per-request AD load).
/// </summary>
public interface IAdAuthenticationService
{
    /// <summary>
    /// True if <paramref name="username"/>/<paramref name="password"/> is a
    /// valid AD credential in the current domain. Never throws — AD
    /// unreachable, off-domain machine, or wrong credentials all just
    /// return false (logged as a warning). On success,
    /// <paramref name="canonicalUsername"/> is set to "DOMAIN\samAccountName".
    /// </summary>
    bool ValidateCredentials(string username, string password, out string canonicalUsername);

    /// <summary>
    /// True if <paramref name="canonicalUsername"/> ("DOMAIN\samAccountName")
    /// is a member of <paramref name="requiredGroup"/>. Lets AD exceptions
    /// propagate — the caller decides how to degrade on failure.
    /// </summary>
    bool IsMemberOfViewerGroup(string canonicalUsername, string requiredGroup);
}
