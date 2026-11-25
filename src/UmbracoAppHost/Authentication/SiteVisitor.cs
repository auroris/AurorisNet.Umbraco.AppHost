using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Security;
using AppBlocks.Authentication;
using IdentityServer.Sso;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// The website member and the independent backoffice session, resolved once per request.
/// </summary>
public sealed class SiteVisitor(IMemberManager memberManager, SsoProviderCatalog providers, IHttpContextAccessor httpContextAccessor,
    ApplicationAccessPolicy access, IOptions<IdentityServerSettings> identityServer)
{
    private bool resolved;

    /// <summary>The display name of the signed-in member; null for an anonymous visitor.</summary>
    public string? Name { get; private set; }

    /// <summary>The account's username, as Umbraco knows it; null for an anonymous visitor.</summary>
    public string? UserName { get; private set; }

    /// <summary>The account's email address, when it has one.</summary>
    public string? Email { get; private set; }

    public bool IsSignedIn => IsMember;

    /// <summary>A member signs out on the website; a backoffice user signs out in the backoffice.</summary>
    public bool IsMember { get; private set; }

    public bool IsBackOfficeUser { get; private set; }

    /// <summary>The sign-in providers to offer members, one button each.</summary>
    public IReadOnlyList<SsoProvider> Providers { get; } = providers.MemberLogins.ToList();

    /// <summary>The configured identity server starts member sign-in; other providers remain explicit choices.</summary>
    public SsoProvider? AutomaticProvider => identityServer.Value is { Enabled: true, MemberEnabled: true } settings
        ? Providers.FirstOrDefault(provider => provider.MemberScheme == settings.MemberScheme)
        : null;

    /// <summary>Only a fresh visit to a sign-in view starts SSO, never a callback or a submitted form.</summary>
    public bool ShouldStartMemberSignIn(bool hasErrors, bool requiresTwoFactor)
    {
        var request = httpContextAccessor.HttpContext?.Request;
        return !IsMember && !IsBackOfficeUser && AutomaticProvider is not null && request is not null
            && HttpMethods.IsGet(request.Method) && !hasErrors && !requiresTwoFactor
            && !request.Query.ContainsKey("ufprt")
            && !request.Query.ContainsKey(SsoLoginUrls.LocalParameter)
            && !request.Query.ContainsKey(SsoRefusal.DeniedParameter);
    }

    /// <summary>The reason a provider put on the query string after refusing a sign-in, if any.</summary>
    public string? Denied =>
        httpContextAccessor.HttpContext?.Request.Query[SsoRefusal.DeniedParameter].ToString() is { Length: > 0 } reason ? reason : null;

    /// <summary>Resolves the visitor; later calls in the same request return at once.</summary>
    public async Task<SiteVisitor> ResolveAsync()
    {
        if (resolved)
            return this;
        resolved = true;

        var context = httpContextAccessor.HttpContext;
        if (context is null)
            return this;

        IsBackOfficeUser = await access.BackOfficeUserAsync(context) is not null;
        if (memberManager.IsLoggedIn())
        {
            var member = await memberManager.GetCurrentMemberAsync();
            if (member is not null)
            {
                Name = string.IsNullOrWhiteSpace(member.Name) ? member.UserName : member.Name;
                UserName = member.UserName;
                Email = member.Email;
                IsMember = true;
            }
        }
        return this;
    }
}
