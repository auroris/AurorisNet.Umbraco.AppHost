using IdentityServer.Sso;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using Umbraco.Cms.Web.Website.Controllers;

namespace UmbracoAppHost.Authentication;

/// <summary>Validates AppHost's member sign-in forms with the same visitor identity that rendered their antiforgery token.</summary>
internal sealed class MemberSignInFormIdentity : IControllerModelConvention, IAsyncAuthorizationFilter
{
    public void Apply(ControllerModel model)
    {
        var controller = model.ControllerType.AsType();
        if (controller == typeof(UmbLoginController) || controller == typeof(UmbTwoFactorLoginController) || controller == typeof(MemberLoginController))
            model.Filters.Add(this);
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsPost(request.Method) && request.Query.ContainsKey(SsoLoginUrls.LocalParameter))
            await context.HttpContext.RequestServices.GetRequiredService<SiteVisitor>().ResolveAsync();
    }
}
