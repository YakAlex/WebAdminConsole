using AdminConsole.Api.Controllers;
using AdminConsole.Tests.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;

namespace AdminConsole.Tests.Controllers;

public sealed class AuthControllerTests
{
    private const string RequiredGroup = "SANTA\\AdminConsole-Admins";

    private static (AuthController controller, RecordingAuthenticationService authService) NewController(
        FakeAdAuthenticationService adAuth)
    {
        var authService = new RecordingAuthenticationService();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(authService);
        var provider = services.BuildServiceProvider();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Authorization:ViewerGroup"] = RequiredGroup })
            .Build();

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var controller = new AuthController(adAuth, config, NullLogger<AuthController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        return (controller, authService);
    }

    [Fact]
    public async Task Login_WithValidCredentials_AndGroupMembership_SignsIn_WithRoleClaim()
    {
        var adAuth = new FakeAdAuthenticationService
        {
            ValidateResult = true, CanonicalUsername = "SANTA\\yakymenko", MembershipResult = true,
        };
        var (controller, authService) = NewController(adAuth);

        var result = await controller.Login(new AuthController.LoginRequest("yakymenko", "correct-password"));

        Assert.IsType<OkResult>(result);
        Assert.NotNull(authService.SignedInPrincipal);
        Assert.True(authService.SignedInPrincipal!.IsInRole(RequiredGroup));
        Assert.Equal("SANTA\\yakymenko", authService.SignedInPrincipal.Identity!.Name);
    }

    [Fact]
    public async Task Login_WithValidCredentials_ButNotGroupMember_StillSignsIn_WithoutRoleClaim()
    {
        // Mirrors the original Negotiate behavior: authentication succeeds
        // for any valid AD account — the "Viewer" policy is what then
        // blocks a non-member on every subsequent API call.
        var adAuth = new FakeAdAuthenticationService
        {
            ValidateResult = true, CanonicalUsername = "SANTA\\someoneelse", MembershipResult = false,
        };
        var (controller, authService) = NewController(adAuth);

        var result = await controller.Login(new AuthController.LoginRequest("someoneelse", "correct-password"));

        Assert.IsType<OkResult>(result);
        Assert.False(authService.SignedInPrincipal!.IsInRole(RequiredGroup));
    }

    [Fact]
    public async Task Login_WhenGroupCheckThrows_StillSignsIn_TreatingItAsNotAMember()
    {
        var adAuth = new FakeAdAuthenticationService
        {
            ValidateResult = true, CanonicalUsername = "SANTA\\yakymenko", ThrowOnMembershipCheck = true,
        };
        var (controller, authService) = NewController(adAuth);

        var result = await controller.Login(new AuthController.LoginRequest("yakymenko", "correct-password"));

        Assert.IsType<OkResult>(result);
        Assert.False(authService.SignedInPrincipal!.IsInRole(RequiredGroup));
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ReturnsUnauthorized_AndDoesNotSignIn()
    {
        var adAuth = new FakeAdAuthenticationService { ValidateResult = false };
        var (controller, authService) = NewController(adAuth);

        var result = await controller.Login(new AuthController.LoginRequest("yakymenko", "wrong-password"));

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Null(authService.SignedInPrincipal);
    }

    [Theory]
    [InlineData("", "something")]
    [InlineData("someone", "")]
    [InlineData("   ", "something")]
    public async Task Login_WithMissingUsernameOrPassword_ReturnsBadRequest(string username, string password)
    {
        var (controller, _) = NewController(new FakeAdAuthenticationService());

        var result = await controller.Login(new AuthController.LoginRequest(username, password));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Logout_SignsOut_AndReturnsOk()
    {
        var (controller, authService) = NewController(new FakeAdAuthenticationService());

        var result = await controller.Logout();

        Assert.IsType<OkResult>(result);
        Assert.True(authService.SignedOutCalled);
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public ClaimsPrincipal? SignedInPrincipal { get; private set; }
        public bool SignedOutCalled { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignedInPrincipal = principal;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOutCalled = true;
            return Task.CompletedTask;
        }
    }
}
