using AppBlocks.Blocks;
using AppBlocks.Publishing;
using Microsoft.AspNetCore.Html;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services.Navigation;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Website;

/// <summary>The resolved site root, effective modification date and short-circuit decision for a layout request.</summary>
public sealed record AppHostPageState(IPublishedContent Site, DateTime UpdateDate, bool ShouldShortCircuit);

/// <summary>
/// Shared layout preamble for website, application and downstream theme layouts: handles page redirects, registers
/// page assets and navigation dependency updates, keeps authenticated responses out of shared caches and applies
/// HTTP cache headers.
/// </summary>
public static class AppHostPage
{
    public static async Task<AppHostPageState> PrepareAsync(
        IPublishedContent page,
        HttpContext context,
        bool applyCacheHeaders = true)
    {
        var services = context.RequestServices;
        var navigation = services.GetRequiredService<IDocumentNavigationQueryService>();
        var published = services.GetRequiredService<IPublishedContentStatusFilteringService>();
        var site = page.SiteRoot(navigation, published);

        var destination = services.GetService<PageDestination>();
        var redirect = destination?.Url(page) ?? SiteContent.Text(page, "link");
        if (redirect is not null)
        {
            context.Response.Redirect(redirect);
            return new(site, page.UpdateDate, ShouldShortCircuit: true);
        }

        services.GetRequiredService<AppPageAssets>().RegisterPage(page, navigation, published, context);
        services.GetService<NavigationUpdates>()?.Register(context);

        if (services.GetService<SiteVisitor>() is { } visitor && (await visitor.ResolveAsync()).IsSignedIn)
            context.KeepOutOfCaches();

        var updateDate = context.GetPageUpdateDate(page.UpdateDate);
        if (applyCacheHeaders && context.ApplyCacheHeaders(updateDate))
            return new(site, updateDate, ShouldShortCircuit: true);

        return new(site, updateDate, ShouldShortCircuit: false);
    }

    /// <summary>Renders any raw HTML <c>&lt;head&gt;</c> tags registered in <c>Context.Items["PageHeaders"]</c>.</summary>
    public static IHtmlContent RenderPageHeaders(HttpContext context)
    {
        if (context.Items["PageHeaders"] is IEnumerable<string> headers)
            return new HtmlString(string.Concat(headers));
        if (context.Items["PageHeaders"] is string header)
            return new HtmlString(header);
        return HtmlString.Empty;
    }
}
