using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using IHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace UmbracoAppHost.Hosting;

/// <summary>Gives each installation its own browser cookies even when several sites share a hostname.</summary>
internal sealed class InstallationCookies
{
    // Umbraco reads these by name, including directly from Request.Cookies, and deletes some of them at logout.
    private readonly Dictionary<string, string> fixedNames;
    private readonly PathString virtualPath;

    public InstallationCookies(IConfiguration configuration, IHostingEnvironment hosting)
    {
        var mount = hosting.ApplicationVirtualPath.Trim('/');
        virtualPath = mount.Length == 0 ? PathString.Empty : new PathString("/" + mount);
        var name = Regex.Replace((hosting.SiteName ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (name.Length == 0) name = "site";
        if (name.Length > 32) name = name[..32].TrimEnd('-');
        var id = configuration["Umbraco:CMS:Global:Id"]?.Trim();
        if (Guid.TryParse(id, out var guid)) id = guid.ToString("D");
        // Hosts using only the IUmbracoBuilder entry point can supply their own instance setup without a Global:Id.
        if (string.IsNullOrEmpty(id)) id = hosting.ApplicationPhysicalPath + "\n" + virtualPath;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        Suffix = "-" + name + "-" + Convert.ToHexStringLower(hash.AsSpan(0, 6));

        string[] names = [UmbracoAppHost.Authentication.MemberSignOut.Cookie, Constants.Web.PreviewCookieName, "UMB-DEBUG", ViewDataExtensions.TokenExternalSignInError,
            Constants.Web.CsrfValidationCookieName, Constants.Security.BackOfficeExternalCookieName];
        fixedNames = names.ToDictionary(key => key, key => key + Suffix, StringComparer.OrdinalIgnoreCase);
    }

    public string Suffix { get; }

    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<InstallationCookies>();
        // Umbraco also uses AuthCookieName to derive the exposed backoffice cookie and when clearing the login.
        services.AddOptions<SecuritySettings>().PostConfigure<InstallationCookies>(
            (options, site) => options.AuthCookieName += site.Suffix);
#pragma warning disable CS0618 // Umbraco 18 still uses this option for access, refresh and PKCE token cookies.
        services.AddOptions<BackOfficeTokenCookieSettings>().PostConfigure<InstallationCookies>(
            (options, site) => options.SiteName += site.Suffix);
#pragma warning restore CS0618
        string[] schemes = [Constants.Security.BackOfficeExternalAuthenticationType, Constants.Security.BackOfficeTwoFactorAuthenticationType,
            Constants.Security.BackOfficeTwoFactorRememberMeAuthenticationType, IdentityConstants.ApplicationScheme, IdentityConstants.ExternalScheme,
            IdentityConstants.TwoFactorUserIdScheme, IdentityConstants.TwoFactorRememberMeScheme];
        foreach (var scheme in schemes)
            services.AddOptions<CookieAuthenticationOptions>(scheme).PostConfigure<InstallationCookies>(
                (options, site) => options.Cookie.Name = (options.Cookie.Name ?? CookieAuthenticationDefaults.CookiePrefix + scheme) + site.Suffix);
        services.AddOptions<SessionOptions>().PostConfigure<InstallationCookies>((options, site) => options.Cookie.Name += site.Suffix);
        services.AddOptions<CookieTempDataProviderOptions>().PostConfigure<InstallationCookies>((options, site) => options.Cookie.Name += site.Suffix);
        services.AddOptions<AntiforgeryOptions>().PostConfigure<InstallationCookies>((options, site) => options.Cookie.Name += site.Suffix);
    }

    public Task Apply(HttpContext context, RequestDelegate next)
    {
        // IIS and Umbraco match mounts ignoring case. Browsers compare cookie paths with case sensitivity.
        // Redirect before any handler can issue or delete a cookie under another spelling of this mount.
        if (virtualPath.HasValue && context.Request.PathBase.Add(context.Request.Path)
                .StartsWithSegments(virtualPath, StringComparison.OrdinalIgnoreCase, out var matched, out var remaining)
            && !matched.Equals(virtualPath, StringComparison.Ordinal))
        {
            context.Response.Redirect(virtualPath.Add(remaining).ToUriComponent() + context.Request.QueryString,
                permanent: false, preserveMethod: true);
            return Task.CompletedTask;
        }
        var incoming = context.Request.Cookies;
        if (fixedNames.Any(pair => incoming.ContainsKey(pair.Key) || incoming.ContainsKey(pair.Value)))
        {
            // Ignore old shared names even during a rolling deployment, when a sibling may still be using them.
            var cookies = new RequestCookies(incoming);
            foreach (var (logical, physical) in fixedNames)
            {
                cookies.Remove(logical);
                if (cookies.Remove(physical, out var value)) cookies[logical] = value;
            }
            context.Features.Set<IRequestCookiesFeature>(new RequestCookiesFeature(cookies));
        }
        context.Features.Set<IResponseCookiesFeature>(new ResponseCookies(context, context.Response.Cookies, this));
        return next(context);
    }

    private sealed class RequestCookies(IRequestCookieCollection source) :
        Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase), IRequestCookieCollection
    {
        ICollection<string> IRequestCookieCollection.Keys => Keys;
        string? IRequestCookieCollection.this[string key] => this.GetValueOrDefault(key);
    }

    private sealed class ResponseCookies(HttpContext context, IResponseCookies inner, InstallationCookies site) : IResponseCookiesFeature, IResponseCookies
    {
        public IResponseCookies Cookies => this;

        public void Append(string key, string value) => Append(key, value, new CookieOptions());
        public void Delete(string key) => Delete(key, new CookieOptions());

        public void Append(string key, string value, CookieOptions options)
        {
            key = site.fixedNames.GetValueOrDefault(key, key);
            inner.Append(key, value, OptionsFor(key, options));
        }

        public void Delete(string key, CookieOptions options)
        {
            key = site.fixedNames.GetValueOrDefault(key, key);
            inner.Delete(key, OptionsFor(key, options));
        }

        private CookieOptions OptionsFor(string key, CookieOptions options)
        {
            // The suffix can precede _EXPOSED or a chunk number. __Host- cookies must keep Path=/.
            if (options.Path != "/" || key.StartsWith("__Host-", StringComparison.Ordinal)
                || !key.Contains(site.Suffix, StringComparison.Ordinal)) return options;
            var path = context.Request.PathBase.HasValue ? context.Request.PathBase.ToUriComponent() : site.virtualPath.ToUriComponent();
            return path.Length == 0 ? options : new CookieOptions(options) { Path = path };
        }
    }
}
