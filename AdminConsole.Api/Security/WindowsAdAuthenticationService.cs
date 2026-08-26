using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;

namespace AdminConsole.Api.Security;

/// <summary>
/// Real AD implementation via System.DirectoryServices.AccountManagement —
/// Windows-only by design, consistent with the rest of the app (Windows
/// Service, DPAPI-NG).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAdAuthenticationService(ILogger<WindowsAdAuthenticationService> logger)
    : IAdAuthenticationService
{
    public bool ValidateCredentials(string username, string password, out string canonicalUsername)
    {
        canonicalUsername = string.Empty;

        try
        {
            var samAccountName = StripDomainPrefix(username);
            using var context = new PrincipalContext(ContextType.Domain);
            if (!context.ValidateCredentials(samAccountName, password))
                return false;

            canonicalUsername = $"{Environment.UserDomainName}\\{samAccountName}";
            return true;
        }
        catch (Exception ex)
        {
            // Machine off-domain / domain controller unreachable / malformed
            // username — all treated as "cannot validate", which the caller
            // (AuthController) turns into a generic 401.
            logger.LogWarning(ex,
                "WindowsAdAuthenticationService: failed to validate credentials for {User} " +
                "— the domain is unreachable, the machine is not domain-joined, or the credentials are invalid.",
                username ?? "(null)");
            return false;
        }
    }

    public bool IsMemberOfViewerGroup(string canonicalUsername, string requiredGroup)
    {
        using var context = new PrincipalContext(ContextType.Domain);
        using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, canonicalUsername);
        return user is not null && user.IsMemberOf(context, IdentityType.SamAccountName, requiredGroup);
    }

    /// <summary>Accepts either "DOMAIN\user" or bare "user" from the login form; AD's ValidateCredentials expects the bare sAMAccountName.</summary>
    public static string StripDomainPrefix(string username)
    {
        var separatorIndex = username.IndexOf('\\');
        return separatorIndex >= 0 ? username[(separatorIndex + 1)..] : username;
    }
}
