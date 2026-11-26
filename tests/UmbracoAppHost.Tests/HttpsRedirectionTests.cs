using System.Net;

namespace UmbracoAppHost.Tests;

[Collection("Umbraco hosts")]
public sealed class HttpsRedirectionTests
{
    [Theory]
    [InlineData(null, HttpStatusCode.OK)]
    [InlineData("false", HttpStatusCode.OK)]
    [InlineData("true", HttpStatusCode.TemporaryRedirect)]
    public async Task Only_an_opted_in_site_redirects_HTTP_and_HTTPS_serves_the_asset(
        string? setting, HttpStatusCode expectedStatus)
    {
        const string asset = "/portal/api/reports/ui/ir.js?next=%2Forders";
        var options = new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            VirtualPath = "/portal",
            StartSettings = { ["https_port"] = "8443", ["ForwardedHeaders_Enabled"] = "true" },
        };
        if (setting is not null) options.StartSettings["UmbracoAppHost:RedirectToHttps"] = setting;
        var ct = TestContext.Current.CancellationToken;
        await using var site = await TestSite.StartAsync(options, ct);
        using var browser = site.CreateClient();
        browser.DefaultRequestHeaders.Host = "portal.example";

        using var http = await browser.GetAsync(asset, ct);
        Assert.Equal(expectedStatus, http.StatusCode);
        if (expectedStatus == HttpStatusCode.TemporaryRedirect)
            Assert.Equal("https://portal.example:8443" + asset, http.Headers.Location?.AbsoluteUri);

        browser.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var https = await browser.GetAsync(asset, ct);
        Assert.Equal(HttpStatusCode.OK, https.StatusCode);
        Assert.False(https.Headers.Contains("Strict-Transport-Security"));
    }
}
