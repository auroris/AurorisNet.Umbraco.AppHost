using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class PreviewCookieTests
{
    [Fact]
    public async Task Entering_and_leaving_preview_only_uses_this_installations_cookie()
    {
        const string route = "/__tests/preview";
        const string url = "/wing" + route;
        await using var site = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            VirtualPath = "/wing",
            FirstBootGroup = "Settings",
            UseInstallSnapshot = false,
            Settings = { ["Umbraco:CMS:Hosting:SiteName"] = "Wing" },
            ConfigureApp = app =>
            {
                app.MapPost(route, async (IPreviewService preview, IUserService users) =>
                    await preview.TryEnterPreviewAsync((await users.GetAsync(Constants.Security.SuperUserKey))!));
                app.MapGet(route, (HttpRequest request) => request.HasPreviewCookie());
                app.MapDelete(route, (IPreviewService preview) => preview.EndPreviewAsync());
            },
        }, TestContext.Current.CancellationToken);
        using var browser = site.CreateClient();
        // A sibling still running the previous version owns the old, root-scoped cookie.
        browser.DefaultRequestHeaders.Add(HeaderNames.Cookie, Constants.Web.PreviewCookieName + "=another-site");
        Assert.False(await browser.GetFromJsonAsync<bool>(url, TestContext.Current.CancellationToken));

        // The browser compares cookie paths case-sensitively. Preserve the operation and its query when
        // a visitor follows a bookmark with another spelling of the mount path.
        using var bookmark = await browser.PostAsync("/WING" + route + "?return=%2FResume", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, bookmark.StatusCode);
        Assert.Equal(url + "?return=%2FResume", bookmark.Headers.Location!.OriginalString);
        using var enter = await browser.PostAsync(bookmark.Headers.Location, null, TestContext.Current.CancellationToken);
        Assert.True(await enter.Content.ReadFromJsonAsync<bool>(TestContext.Current.CancellationToken));
        var issued = PreviewCookie(enter);
        Assert.Matches("^UMB_PREVIEW-wing-[0-9a-f]{12}$", issued.Name.ToString());
        Assert.Equal("/wing", issued.Path.ToString());
        Assert.True(issued.HttpOnly);
        Assert.True(await browser.GetFromJsonAsync<bool>(url, TestContext.Current.CancellationToken));

        using var leave = await browser.DeleteAsync(url, TestContext.Current.CancellationToken);
        leave.EnsureSuccessStatusCode();
        var cleared = PreviewCookie(leave);
        Assert.Equal(issued.Name, cleared.Name);
        Assert.Equal(issued.Path, cleared.Path);
        Assert.True(cleared.Expires <= DateTimeOffset.UtcNow);
        Assert.False(await browser.GetFromJsonAsync<bool>(url, TestContext.Current.CancellationToken));
    }

    private static SetCookieHeaderValue PreviewCookie(HttpResponseMessage response)
        => Assert.Single(response.Headers.GetValues(HeaderNames.SetCookie).Select(value => SetCookieHeaderValue.Parse(value)),
            cookie => cookie.Name.StartsWith(Constants.Web.PreviewCookieName, StringComparison.Ordinal));
}
