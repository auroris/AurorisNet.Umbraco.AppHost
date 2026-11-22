using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Umbraco.Cms.Core.Configuration.Models;

namespace UmbracoAppHost.Tests;

internal static class LoginCookieAssertions
{
    internal static bool SetsBackOfficeLoginCookie(this HttpResponseMessage response, IServiceProvider services)
    {
        var name = services.GetRequiredService<IOptions<SecuritySettings>>().Value.AuthCookieName;
        return response.Headers.TryGetValues(HeaderNames.SetCookie, out var headers)
            && headers.Select(header => SetCookieHeaderValue.Parse(header)).Any(cookie => cookie.Name == name && cookie.Value.Length > 0);
    }
}
