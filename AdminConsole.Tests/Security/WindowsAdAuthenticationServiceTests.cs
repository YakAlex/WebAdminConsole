using AdminConsole.Api.Security;

namespace AdminConsole.Tests.Security;

public sealed class WindowsAdAuthenticationServiceTests
{
    [Theory]
    [InlineData("SANTA\\yakymenko", "yakymenko")]
    [InlineData("yakymenko", "yakymenko")]
    [InlineData("DOMAIN\\some.user", "some.user")]
    public void StripDomainPrefix_RemovesLeadingDomainPrefixIfPresent(string input, string expected)
    {
        Assert.Equal(expected, WindowsAdAuthenticationService.StripDomainPrefix(input));
    }
}
