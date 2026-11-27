using UmbracoAppHost.Website;

namespace UmbracoAppHost.Tests;

public sealed class PageDestinationTests
{
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData(" JavaScript:alert(1)")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/ok\u0007")]
    [InlineData("/ok\n/next")]
    [InlineData("data:text/html,hi")]
    [InlineData("mailto:someone@example.invalid")]
    [InlineData("relative/path")]
    [InlineData("https://ok.example\\@evil.example/")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_link_that_is_not_a_local_path_or_an_HTTP_URL_is_refused(string? link)
        => Assert.Null(PageDestination.Safe(link));

    [Theory]
    [InlineData("https://ok.example/x", "https://ok.example/x")]
    [InlineData("http://ok.example/", "http://ok.example/")]
    [InlineData("/relative?x=1#y", "/relative?x=1#y")]
    [InlineData("  /trimmed/  ", "/trimmed/")]
    public void A_local_path_or_an_HTTP_URL_is_kept(string link, string expected)
        => Assert.Equal(expected, PageDestination.Safe(link));
}
