namespace AdminConsole.Tests.Security;

/// <summary>
/// In-memory double for IAdAuthenticationService — avoids depending on a
/// real Active Directory domain controller in unit tests, mirroring the
/// FakeDataProtector pattern in CredentialStoreTests.
/// </summary>
public sealed class FakeAdAuthenticationService : AdminConsole.Api.Security.IAdAuthenticationService
{
    public bool ValidateResult { get; set; }
    public string CanonicalUsername { get; set; } = "SANTA\\yakymenko";
    public bool MembershipResult { get; set; }
    public bool ThrowOnMembershipCheck { get; set; }

    public bool ValidateCredentials(string username, string password, out string canonicalUsername)
    {
        canonicalUsername = CanonicalUsername;
        return ValidateResult;
    }

    public bool IsMemberOfViewerGroup(string canonicalUsername, string requiredGroup)
    {
        if (ThrowOnMembershipCheck) throw new InvalidOperationException("simulated AD failure");
        return MembershipResult;
    }
}
