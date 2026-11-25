using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Web.Common.Security;
using AppBlocks.Authentication;

namespace UmbracoAppHost.Authentication;

[Route("application/account")]
public sealed class ApplicationAccountController(ApplicationAccessPolicy access, IAntiforgery antiforgery,
    IMemberSignInManager members) : Controller
{
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromForm] string? returnUrl)
    {
        var member = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var backOfficeUser = await access.BackOfficeUserAsync(HttpContext);
        var principal = member.Succeeded ? member.Principal : backOfficeUser;
        if (principal is not null) HttpContext.User = principal;
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return BadRequest(); }

        if (member.Succeeded) await members.SignOutAsync();
        Response.Cookies.Append(MemberSignOut.Cookie, "1", MemberSignOut.Options(Request));
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : Request.PathBase + "/");
    }
}
