using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Navigation;

namespace UmbracoAppHost.Website;

/// <summary>
/// Which cultures fall back to other languages, read from Umbraco's language settings: every culture other than the
/// default whose language names a fallback language. The languages are read on first use and again after a change.
/// </summary>
public sealed class LanguageFallbacks(ILanguageService languages, IVariationContextAccessor variations)
{
    private volatile IReadOnlyDictionary<string, string[]>? chains;

    /// <summary>
    /// The culture followed by the languages it falls back to, in order, or empty when the culture is the default or
    /// names no fallback. Without <paramref name="culture"/>, the culture of the current request.
    /// </summary>
    public IReadOnlyList<string> Chain(string? culture = null)
    {
        culture ??= variations.VariationContext?.Culture;
        if (string.IsNullOrEmpty(culture)) return [];
        var known = chains ??= Chains(languages.GetAllAsync().GetAwaiter().GetResult()
            .Select(language => (language.IsoCode, language.IsDefault, language.FallbackIsoCode)));
        return known.TryGetValue(culture, out var chain) ? chain : [];
    }

    /// <summary>Whether <paramref name="culture"/>, or the request's culture, falls back to another language.</summary>
    public bool Applies(string? culture = null) => Chain(culture).Count > 0;

    /// <summary>Forgets the languages read so far; the next call reads them again.</summary>
    internal void Reset() => chains = null;

    /// <summary>
    /// For each culture other than the default whose language names a fallback, the culture followed by its fallback
    /// languages in order. The chain stops at a language without a fallback, at an unknown language and at a loop.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Chains(IEnumerable<(string IsoCode, bool IsDefault, string? FallbackIsoCode)> languages)
    {
        var byCode = new Dictionary<string, (string IsoCode, bool IsDefault, string? FallbackIsoCode)>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in languages) byCode[language.IsoCode] = language;
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in byCode.Values.Where(language => !language.IsDefault))
        {
            var chain = new List<string> { language.IsoCode };
            for (var next = language.FallbackIsoCode;
                 next is not null && byCode.TryGetValue(next, out var fallback) && !chain.Contains(fallback.IsoCode, StringComparer.OrdinalIgnoreCase);
                 next = fallback.FallbackIsoCode)
                chain.Add(fallback.IsoCode);
            if (chain.Count > 1) result[language.IsoCode] = [.. chain];
        }
        return result;
    }
}

/// <summary>Reads the languages again after one is saved or deleted on any server.</summary>
public sealed class LanguageFallbacksRefresh(LanguageFallbacks fallbacks) : INotificationHandler<LanguageCacheRefresherNotification>
{
    public void Handle(LanguageCacheRefresherNotification notification) => fallbacks.Reset();
}

/// <summary>
/// Serves a missing translation from the language the culture's Umbraco language falls back to (see
/// <see cref="LanguageFallbacks"/>). A page keeps its own URL segment and published content in that culture; a page
/// published only in a fallback language is served under the culture's domain, with the fallback language's segment
/// and content. Drafts stay unpublished in every culture.
/// </summary>
public sealed class CultureFallback(LanguageFallbacks languages, IDocumentNavigationQueryService navigation,
    IDocumentPublishStatusQueryService status, IDocumentUrlService segments, IDomainCache domains,
    IPublishedContentCache cache, IHttpContextAccessor http, IOptions<HostingSettings> hosting)
{
    private static readonly object SiblingSegments = new();

    /// <inheritdoc cref="LanguageFallbacks.Chain"/>
    public IReadOnlyList<string> Chain(string? culture = null) => languages.Chain(culture);

    /// <summary>The request's site address, for <see cref="Route"/>.</summary>
    public Uri Current => http.HttpContext is { } context
        ? new Uri($"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/") : new Uri("http://localhost/");

    /// <summary>The first culture of <paramref name="chain"/> the page is published in, or null.</summary>
    private string? ServedFrom(Guid key, IReadOnlyList<string> chain) => chain.FirstOrDefault(culture => status.IsPublished(key, culture));

    /// <summary>
    /// The page's URL segment in <paramref name="culture"/>: its own when it is published in that culture, otherwise
    /// the segment of the language it is served from, made unique among its siblings by <see cref="AllocateSegments"/>.
    /// </summary>
    public string? Segment(Guid key, string culture)
    {
        var chain = Chain(culture);
        if (chain.Count == 0) return segments.GetUrlSegment(key, culture, false);
        if (status.IsPublished(key, chain[0])) return segments.GetUrlSegment(key, chain[0], false);
        if (!navigation.TryGetParentKey(key, out var parent) || parent is null)
            return ServedFrom(key, chain) is { } root ? segments.GetUrlSegment(key, root, false) : null;
        var items = http.HttpContext?.Items;
        var known = items?[SiblingSegments] as Dictionary<(string, Guid), Dictionary<Guid, string>>;
        if (known is null && items is not null) items[SiblingSegments] = known = [];
        var siblingsKey = (chain[0].ToLowerInvariant(), parent.Value);
        if (known is not null && known.TryGetValue(siblingsKey, out var mapped)) return mapped.GetValueOrDefault(key);
        if (!navigation.TryGetChildrenKeys(parent.Value, out var children)) return null;
        mapped = AllocateSegments(children.Select(child => (Key: child, Culture: ServedFrom(child, chain)))
            .Where(child => child.Culture is not null)
            .Select(child => (child.Key, segments.GetUrlSegment(child.Key, child.Culture!, false),
                string.Equals(child.Culture, chain[0], StringComparison.OrdinalIgnoreCase) ? null : child.Culture)));
        if (known is not null) known[siblingsKey] = mapped;
        return mapped.GetValueOrDefault(key);
    }

    /// <summary>
    /// Gives each sibling a unique segment. A translated page (<c>FallbackCulture</c> null) keeps its own segment.
    /// A page served from a fallback language keeps that language's segment unless an earlier sibling holds it; it
    /// then gets the segment with the language's code appended, numbered past any sibling's own segment.
    /// </summary>
    public static Dictionary<Guid, string> AllocateSegments(IEnumerable<(Guid Key, string? Segment, string? FallbackCulture)> siblings)
    {
        var nodes = siblings.Where(n => !string.IsNullOrEmpty(n.Segment))
            .OrderBy(n => n.FallbackCulture is null ? 0 : 1).ThenBy(n => n.Key).ToArray();
        var reserved = nodes.Select(n => n.Segment!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<Guid, string>();
        foreach (var node in nodes)
        {
            var slug = node.Segment!;
            if (node.FallbackCulture is not null && assigned.Contains(slug))
            {
                // A published translation owns its own URL. Keep every other sibling's own segment available too.
                var suffix = slug + "-" + node.FallbackCulture.ToLowerInvariant();
                slug = suffix;
                for (var i = 2; reserved.Contains(slug) || assigned.Contains(slug); i++) slug = suffix + "-" + i;
            }
            assigned.Add(slug);
            result.Add(node.Key, slug);
        }
        return result;
    }

    /// <summary>Whether the page has a URL in <paramref name="culture"/> on the current request's site.</summary>
    public bool CanServe(Guid key, string culture) => Route(key, culture, Current) is not null;

    /// <summary>
    /// The page's URL in <paramref name="culture"/>, beneath the nearest domain assigned to that culture, or null when
    /// no such domain covers the page, the page or an ancestor below that domain is published in none of the culture's
    /// languages, or a segment is missing.
    /// </summary>
    public Uri? Route(Guid key, string culture, Uri current) => Resolve(key, culture, current, out _);

    /// <summary>
    /// Whether a page may be listed in <paramref name="culture"/>: it is published in one of the culture's languages and,
    /// beneath a domain assigned to the culture, has a route there.
    /// </summary>
    public bool IsAvailable(Guid key, string culture)
    {
        var chain = Chain(culture);
        if (chain.Count == 0 || ServedFrom(key, chain) is null) return false;
        return Resolve(key, culture, Current, out var covered) is not null || !covered;
    }

    private Uri? Resolve(Guid key, string culture, Uri current, out bool covered)
    {
        covered = false;
        var chain = Chain(culture);
        if (chain.Count == 0) return null;
        var ancestry = new List<Guid>();
        var visited = new HashSet<Guid>();
        while (visited.Add(key))
        {
            ancestry.Add(key);
            if (!navigation.TryGetParentKey(key, out var parent) || parent is null) break;
            key = parent.Value;
        }
        var assigned = domains.GetAll(false).Where(d => string.Equals(d.Culture, chain[0], StringComparison.OrdinalIgnoreCase))
            .Select(d => (Domain: d, Root: cache.GetById(false, d.ContentId)))
            .Where(d => d.Root is not null && ancestry.Contains(d.Root.Key))
            .OrderBy(d => ancestry.IndexOf(d.Root!.Key));
        foreach (var assignment in assigned)
        {
            covered = true;
            var rootIndex = ancestry.IndexOf(assignment.Root!.Key);
            if (ancestry.Take(rootIndex + 1).Any(k => ServedFrom(k, chain) is null)) return null;
            var pieces = ancestry.Take(rootIndex).Reverse().Select(k => Segment(k, chain[0])).ToArray();
            if (pieces.Any(string.IsNullOrWhiteSpace)) return null;
            // Umbraco reads a domain, "/fr" as much as "example.com/fr", below the application's virtual path, and adds
            // that path to the URLs it gives out; a URL from here needs it too.
            var name = assignment.Domain.Name;
            var domainUri = name.StartsWith('/') ? new Uri(current, name)
                : new Uri(name.Contains("://") ? name : current.Scheme + "://" + name);
            var virtualPath = hosting.Value.ApplicationVirtualPath?.Trim().TrimEnd('/') ?? "";
            if (virtualPath.Length > 0 && !virtualPath.StartsWith('/')) virtualPath = "/" + virtualPath;
            return new Uri(domainUri.GetLeftPart(UriPartial.Authority) + virtualPath + domainUri.AbsolutePath.TrimEnd('/') + "/"
                + string.Join("/", pieces.Select(p => Uri.EscapeDataString(Uri.UnescapeDataString(p!)))) + (pieces.Length == 0 ? "" : "/"));
        }
        return null;
    }
}

/// <summary>Gives every page a URL in a culture with a fallback language, beneath the culture's domain.</summary>
public sealed class CultureFallbackUrlProvider(CultureFallback fallback) : IUrlProvider
{
    public string Alias => "appHostCultureFallback";

    public UrlInfo? GetUrl(IPublishedContent content, UrlMode mode, string? culture, Uri current)
    {
        var chain = fallback.Chain(culture);
        if (chain.Count == 0 || fallback.Route(content.Key, chain[0], current) is not { } uri) return null;
        var url = mode == UrlMode.Absolute || uri.Authority != current.Authority ? uri.AbsoluteUri : uri.PathAndQuery;
        return UrlInfo.AsUrl(url, Alias, chain[0]);
    }

    public IEnumerable<UrlInfo> GetOtherUrls(int id, Uri current) => [];

    public Task<UrlInfo?> GetPreviewUrlAsync(IContent content, string? culture, string? segment) => Task.FromResult<UrlInfo?>(null);
}

/// <summary>
/// Finds a page by the URL <see cref="CultureFallback"/> gives it, after Umbraco's own lookup by URL found none: under
/// a domain of a culture with a fallback language, each segment selects the one sibling that holds it.
/// </summary>
public sealed class CultureFallbackContentFinder(CultureFallback fallback, IDocumentNavigationQueryService navigation,
    IPublishedContentCache cache) : IContentFinder
{
    public Task<bool> TryFindContent(IPublishedRequestBuilder request)
    {
        var chain = fallback.Chain(request.Culture ?? "");
        if (chain.Count == 0 || request.Domain is null) return Task.FromResult(false);
        var culture = chain[0];
        var node = cache.GetById(false, request.Domain.ContentId);
        if (node is null) return Task.FromResult(false);
        var path = DomainUtilities.PathRelativeToDomain(request.Domain.Uri, request.AbsolutePathDecoded);
        foreach (var piece in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!navigation.TryGetChildrenKeys(node.Key, out var children)) return Task.FromResult(false);
            var matches = children.Where(key => string.Equals(fallback.Segment(key, culture), piece, StringComparison.OrdinalIgnoreCase)
                && fallback.CanServe(key, culture)).Take(2).ToArray();
            if (matches.Length != 1) return Task.FromResult(false);
            node = cache.GetById(false, matches[0]);
            if (node is null) return Task.FromResult(false);
        }
        if (!fallback.CanServe(node.Key, culture)) return Task.FromResult(false);
        request.SetPublishedContent(node);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Lists, in a culture with a fallback language, the pages <see cref="CultureFallback"/> serves, so navigation follows
/// the same rules as the routes. Other cultures and previews keep Umbraco's filtering; drafts stay unlisted.
/// </summary>
public sealed class CultureFallbackStatusFilter(IPublishedContentStatusFilteringService native,
    CultureFallback fallback, IPreviewService preview, IPublishedContentCache cache) : IPublishedContentStatusFilteringService
{
    public IEnumerable<IPublishedContent> FilterAvailable(IEnumerable<Guid> candidateKeys, string? culture)
    {
        var chain = fallback.Chain(culture);
        if (chain.Count == 0 || preview.IsInPreview()) return native.FilterAvailable(candidateKeys, culture);
        return candidateKeys.Where(key => fallback.IsAvailable(key, chain[0]))
            .Select(key => cache.GetById(false, key)).OfType<IPublishedContent>();
    }

    // Umbraco's navigation extensions still call this member, which has no default implementation.
    public IEnumerable<IPublishedContent> Unfiltered(IEnumerable<Guid> candidateKeys)
        => candidateKeys.Select(key => cache.GetById(preview.IsInPreview(), key)).OfType<IPublishedContent>();
}

/// <summary>
/// Makes the language fallback the default for property reads in a culture with a fallback language, so a value
/// missing in that culture comes from its fallback language. A read that names its own fallback keeps it.
/// </summary>
public sealed class CultureValueFallback(IPublishedValueFallback native, LanguageFallbacks cultures) : IPublishedValueFallback
{
    private Fallback Policy(Fallback requested, string? culture) => !requested.HasPolicies && cultures.Applies(culture)
        ? Fallback.ToLanguage : requested;

    public bool TryGetValue(IPublishedProperty property, string? culture, string? segment, Fallback fallback, object? defaultValue, out object? value)
        => native.TryGetValue(property, culture, segment, Policy(fallback, culture), defaultValue, out value);
    public bool TryGetValue<T>(IPublishedProperty property, string? culture, string? segment, Fallback fallback, T? defaultValue, out T? value)
        => native.TryGetValue(property, culture, segment, Policy(fallback, culture), defaultValue, out value);
    public bool TryGetValue(IPublishedElement content, string alias, string? culture, string? segment, Fallback fallback, object? defaultValue, out object? value)
        => native.TryGetValue(content, alias, culture, segment, Policy(fallback, culture), defaultValue, out value);
    public bool TryGetValue<T>(IPublishedElement content, string alias, string? culture, string? segment, Fallback fallback, T? defaultValue, out T? value)
        => native.TryGetValue(content, alias, culture, segment, Policy(fallback, culture), defaultValue, out value);
    public bool TryGetValue(IPublishedContent content, string alias, string? culture, string? segment, Fallback fallback, object? defaultValue, out object? value, out IPublishedProperty? noValueProperty)
        => native.TryGetValue(content, alias, culture, segment, Policy(fallback, culture), defaultValue, out value, out noValueProperty);
    public bool TryGetValue<T>(IPublishedContent content, string alias, string? culture, string? segment, Fallback fallback, T defaultValue, out T? value, out IPublishedProperty? noValueProperty)
        => native.TryGetValue(content, alias, culture, segment, Policy(fallback, culture), defaultValue, out value, out noValueProperty);
    public IDisposable? EnterFallbackScope(Fallback fallback) => native.EnterFallbackScope(fallback);
}
