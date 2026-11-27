using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Services;

namespace UmbracoAppHost.Tests;

/// <summary>
/// French falls back to English in the saved export. Under a French domain, a translated page keeps its French URL and
/// content, an English-only page is served under its English segment with its English content, and an English-only
/// sibling whose segment a translation holds takes the <c>-en</c> suffix. Drafts stay unpublished.
/// </summary>
[Collection("Umbraco hosts")]
public sealed class CultureFallbackHttpTests
{
    private const string English = "en.fallback.test";
    private const string French = "fr.fallback.test";

    [Theory]
    [InlineData("")]
    [InlineData("/portal")]
    [ResetPublishedContentServices]
    public async Task A_French_URL_serves_the_French_page_or_the_English_one_where_the_translation_is_missing(string prefix)
    {
        await using var host = await TestSite.StartAsync(new TestSiteOptions
        {
            Application = typeof(Program).Assembly,
            VirtualPath = prefix,
            Settings = { ["IdentityServer:Enabled"] = "false" },
        }, TestContext.Current.CancellationToken);
        using var scope = host.CreateScope();
        var services = scope.ServiceProvider;
        var types = services.GetRequiredService<IContentTypeService>();
        var contents = services.GetRequiredService<IContentService>();

#pragma warning disable CS0618
        IContent Create(string alias, int parent, string? english, string? french, string title, string? frenchTitle = null, bool publish = true)
        {
            var page = contents.Create(english ?? french!, parent, alias);
            page.TemplateId = types.Get(alias)!.DefaultTemplate?.Id;
            if (english is not null) { page.SetCultureName(english, "en"); page.SetValue("title", title, "en"); }
            if (french is not null) { page.SetCultureName(french, "fr"); page.SetValue("title", frenchTitle ?? title, "fr"); }
            Assert.True(contents.Save(page).Success);
            if (publish)
            {
                var result = contents.Publish(page, english is not null && french is not null ? ["*"] : [english is not null ? "en" : "fr"]);
                Assert.True(result.Success, string.Join(" ", result.EventMessages?.GetAll().Select(message => message.Message) ?? []));
            }
            return page;
        }
        var site = Create("appHostWebsite", Constants.System.Root, "Fallback website", "Site de repli", "Fallback home", "Accueil de repli");
        var domains = await services.GetRequiredService<IDomainService>().UpdateDomainsAsync(site.Key, new DomainsUpdateModel
        {
            DefaultIsoCode = "en",
            Domains = [new DomainModel { DomainName = English, IsoCode = "en" }, new DomainModel { DomainName = French, IsoCode = "fr" }],
        });
        Assert.True(domains.Success);
        Create("webPage", site.Id, "Orders", "Commandes", "Orders in English", "Commandes traduites");
        Create("webPage", site.Id, "Guide", null, "English guide");
        Create("webPage", site.Id, "Commandes", null, "English page named commandes");
        Create("webPage", site.Id, "Draft", "Brouillon", "Draft page", publish: false);
#pragma warning restore CS0618

        using var english = Client(host.Address, English);
        Assert.Contains("Orders in English", await Page(english, prefix + "/orders/"));
        Assert.Contains("English page named commandes", await Page(english, prefix + "/commandes/"));

        using var french = Client(host.Address, French);
        var translated = await Page(french, prefix + "/commandes/");
        Assert.Contains("Commandes traduites", translated);
        Assert.DoesNotContain("Orders in English", translated);
        Assert.Contains("English guide", await Page(french, prefix + "/guide/"));
        Assert.Contains("English page named commandes", await Page(french, prefix + "/commandes-en/"));
        Assert.Equal(HttpStatusCode.NotFound, (await french.GetAsync(prefix + "/brouillon/", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await french.GetAsync(prefix + "/orders/", TestContext.Current.CancellationToken)).StatusCode);

        // The French home lists the English-only pages under their French-site URLs, beneath the site's virtual path.
        var home = await Page(french, prefix + "/");
        Assert.Contains("Accueil de repli", home);
        foreach (var path in new[] { "/commandes/", "/guide/", "/commandes-en/" })
            Assert.Matches("href=\"(?:http://" + Regex.Escape(French) + ")?" + Regex.Escape(prefix + path) + "\"", home);
        Assert.DoesNotContain("/brouillon/", home);
    }

    private static HttpClient Client(Uri address, string host)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address };
        client.DefaultRequestHeaders.Host = host;
        return client;
    }

    private static async Task<string> Page(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {client.DefaultRequestHeaders.Host}{path} returned {response.StatusCode}: {html}");
        return html;
    }
}
