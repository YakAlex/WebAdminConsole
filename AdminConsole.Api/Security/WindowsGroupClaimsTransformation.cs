using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Authentication;

namespace AdminConsole.Api.Security;

/// <summary>
/// Мапить членство в AD-групі (Authorization:ViewerGroup — "AdminConsole-Admins")
/// у ClaimTypes.Role. На IIS `WindowsPrincipal` мав ролі = AD-групи "з коробки";
/// у Kestrel + Negotiate поза IIS групова приналежність НЕ мапиться в claims
/// автоматично (R4) — без цього класу авторизація або пропустить усіх,
/// або не пустить нікого.
///
/// Викликається на кожен автентифікований запит (framework не кешує
/// IClaimsTransformation між запитами); захист від повторної трансформації
/// того самого principal — перевірка вже доданого claim'а на початку.
///
/// Windows-only за дизайном (WindowsIdentity, System.DirectoryServices.AccountManagement) —
/// узгоджено з рештою застосунку (Windows Service, DPAPI-NG, майбутні WMI/quser у Фазі 4).
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
            ?? throw new InvalidOperationException("Authorization:ViewerGroup не сконфігуровано.");
        _logger = logger;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not WindowsIdentity identity || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        if (principal.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == _requiredGroup))
            return Task.FromResult(principal); // вже трансформовано цього запиту

        bool isMember;
        try
        {
            using var context = new PrincipalContext(ContextType.Domain);
            using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, identity.Name);
            isMember = user is not null && user.IsMemberOf(context, IdentityType.SamAccountName, _requiredGroup);
        }
        catch (Exception ex)
        {
            // Машина поза доменом / контролер домену недоступний — трактуємо як
            // "не можемо підтвердити членство" (у підсумку 403 нижче по пайплайну),
            // а НЕ як 500. Це водночас і легітимний production-ризик (R1 — сервіс
            // під локальним, а не gMSA-акаунтом), і очікуваний сценарій локального
            // тестування поза AD (недоменна машина розробника).
            _logger.LogWarning(ex,
                "WindowsGroupClaimsTransformation: не вдалось перевірити членство {User} в AD-групі {Group} " +
                "— домен недоступний або машина не приєднана до домену.",
                identity.Name, _requiredGroup);
            return Task.FromResult(principal);
        }

        if (!isMember)
            return Task.FromResult(principal);

        // WindowsIdentity.RoleClaimType за замовчуванням вказує на
        // ClaimTypes.GroupSid, а НЕ на ClaimTypes.Role — це властивість,
        // яку Clone() успадковує і яку неможливо змінити пост-фактум.
        // Тому AddClaim(ClaimTypes.Role, ...) напряму в клон WindowsIdentity
        // НЕ працює: User.IsInRole(...)/RequireRole(...) шукають claim
        // виключно за identity.RoleClaimType, який лишається GroupSid
        // незалежно від того, які claims туди додати (перевірено емпірично:
        // claim видно в User.Claims, а IsInRole все одно повертає false).
        //
        // Виправлення — окрема, звичайна ClaimsIdentity з явним
        // RoleClaimType = ClaimTypes.Role. ClaimsPrincipal.IsInRole()
        // перевіряє ВСІ ClaimsIdentity в principal, тож досить додати цю
        // другу ідентичність поруч із оригінальною WindowsIdentity.
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
