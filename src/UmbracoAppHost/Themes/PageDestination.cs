using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Routing;

namespace UmbracoAppHost.Website;

/// <summary>
/// Resolves a page's redirect destination from its <c>destination</c> link picker or legacy <c>link</c> text property,
/// validating that the target is a safe relative path or HTTP(S) URL.
/// </summary>
public sealed class PageDestination(IPublishedContentCache cache, IPublishedUrlProvider urls, IVariationContextAccessor variation)
{
    public string? Url(IPublishedContent page, string? culture = null)
    {
        var link = page.Value<Link>("destination", culture: culture);
        var destination = link?.Url ?? page.Value<string>("link", culture: culture);
        if (link is { Type: LinkType.Content, Udi: GuidUdi udi })
        {
            // A fallback property's converted Link may be cached in the fallback culture. Resolve its identity
            // for this request rather than reusing the cached URL from another language.
            var target = cache.GetById(false, udi.Guid);
            var suffixStart = link.Url?.IndexOfAny(['?', '#']) ?? -1;
            destination = target?.Url(urls, culture ?? variation.VariationContext?.Culture, UrlMode.Relative);
            if (destination is not null && suffixStart >= 0) destination += link.Url![suffixStart..];
        }
        return Safe(destination);
    }

    /// <summary>
    /// <paramref name="link"/>, trimmed, when it is a path on this host or an HTTP(S) URL; otherwise null. Every use of a
    /// page's <c>link</c> text, as a redirect or as an <c>href</c>, goes through this, so a value it refuses
    /// (<c>javascript:</c>, <c>//host</c>, a backslash, a control character) is never followed.
    /// </summary>
    public static string? Safe(string? link)
    {
        if (string.IsNullOrWhiteSpace(link) || link.Any(char.IsControl)) return null;
        link = link.Trim();
        if (link.StartsWith('/') && !link.StartsWith("//") && !link.Contains('\\')) return link;
        return !link.Contains('\\') && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? link : null;
    }
}
