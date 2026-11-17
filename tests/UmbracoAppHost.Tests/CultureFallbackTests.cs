using UmbracoAppHost.Website;

namespace UmbracoAppHost.Tests;

public sealed class CultureFallbackTests
{
    [Fact]
    public void A_culture_other_than_the_default_with_a_fallback_language_takes_the_fallback()
    {
        var chains = LanguageFallbacks.Chains([("en", true, null), ("fr", false, "en"), ("es", false, "en"), ("de", false, null)]);
        Assert.Equal(["fr", "en"], chains["fr"]);
        Assert.Equal(["es", "en"], chains["ES"]);
        Assert.False(chains.ContainsKey("en"));
        Assert.False(chains.ContainsKey("de"));
    }

    [Fact]
    public void A_chain_follows_each_fallback_and_stops_at_a_loop_or_an_unknown_language()
    {
        var chains = LanguageFallbacks.Chains([("en", true, "fr"), ("fr", false, "en"), ("pt", false, "es"), ("es", false, "fr"),
            ("it", false, "removed"), ("a", false, "b"), ("b", false, "a")]);
        Assert.Equal(["pt", "es", "fr", "en"], chains["pt"]);
        Assert.Equal(["fr", "en"], chains["fr"]);
        Assert.Equal(["a", "b"], chains["a"]);
        Assert.False(chains.ContainsKey("en"));
        Assert.False(chains.ContainsKey("it"));
    }

    [Fact]
    public void A_translated_page_keeps_its_segment_and_a_colliding_fallback_page_takes_the_language_suffix()
    {
        var translated = Guid.NewGuid();
        var fallback = Guid.NewGuid();
        var sibling = Guid.NewGuid();
        var paths = CultureFallback.AllocateSegments([(fallback, "orders", "en"), (sibling, "orders-en", "en"), (translated, "orders", null)]);
        Assert.Equal("orders", paths[translated]);
        Assert.Equal("orders-en", paths[sibling]);
        Assert.Equal("orders-en-2", paths[fallback]);
    }

    [Fact]
    public void Fallback_pages_without_a_collision_keep_their_segments_and_the_suffix_names_the_language_served()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var guide = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var paths = CultureFallback.AllocateSegments([(second, "news", "fr"), (first, "news", "en"), (guide, "guide", "en"), (missing, null, "en")]);
        Assert.Equal("news", paths[first]);
        Assert.Equal("news-fr", paths[second]);
        Assert.Equal("guide", paths[guide]);
        Assert.False(paths.ContainsKey(missing));
    }
}
