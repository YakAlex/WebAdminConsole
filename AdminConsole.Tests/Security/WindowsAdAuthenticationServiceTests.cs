using AdminConsole.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Fact]
    public void ValidateCredentials_WithNullUsername_ReturnsFalseWithoutThrowing()
    {
        var service = new WindowsAdAuthenticationService(NullLogger<WindowsAdAuthenticationService>.Instance);

        // This must return false and not throw, even though username is null
        var result = service.ValidateCredentials(null!, "anyPassword", out var canonicalUsername);

        Assert.False(result);
        Assert.Equal(string.Empty, canonicalUsername);
    }
}
